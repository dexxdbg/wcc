using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Wcc;

/// <summary>
/// Locates ffmpeg.exe, downloading a static build to %LOCALAPPDATA%\WCC if needed.
///
/// Speed tricks:
///   - Source is BtbN's "latest" rolling release on GitHub, which is served by
///     GitHub's CDN (generally much faster than gyan.dev from most regions).
///   - Download uses up to 8 parallel HTTP Range requests when the server
///     supports partial content (it does). Most CDNs rate-limit per TCP
///     connection, so parallel chunks often give 2-5x throughput.
///   - Progress line shows percent, MB, MB/s, and ETA.
/// </summary>
internal static class FFmpegManager
{
    // BtbN "latest" is a rolling tag maintained by a bot; always points at a
    // recent master build. GPL build includes libx264/libx265/libvpx/libopus/etc.
    private const string DownloadUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    private const int ParallelChunks = 8;

    public static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WCC");

    public static string BundledFfmpegPath => Path.Combine(AppDataDir, "ffmpeg.exe");

    /// <summary>Returns a usable ffmpeg path, or null if none available.</summary>
    public static string? Locate()
    {
        if (File.Exists(BundledFfmpegPath))
            return BundledFfmpegPath;

        var onPath = FindOnPath("ffmpeg.exe");
        return onPath;
    }

    /// <summary>
    /// Copy a user-supplied ffmpeg.exe into the bundled location, skipping
    /// any download. Lets the user avoid the ~80 MB fetch if they already
    /// have ffmpeg somewhere.
    /// </summary>
    public static int SetFromPath(string userPath)
    {
        if (string.IsNullOrWhiteSpace(userPath))
        {
            Console.Error.WriteLine("Usage: wcc set-ffmpeg <path-to-ffmpeg.exe>");
            return 1;
        }

        userPath = Path.GetFullPath(userPath);
        if (!File.Exists(userPath))
        {
            Console.Error.WriteLine($"File not found: {userPath}");
            return 2;
        }

        if (!string.Equals(Path.GetFileName(userPath), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Expected a file named 'ffmpeg.exe', got '{Path.GetFileName(userPath)}'.");
            return 3;
        }

        Directory.CreateDirectory(AppDataDir);

        try
        {
            File.Copy(userPath, BundledFfmpegPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Copy failed: {ex.Message}");
            return 4;
        }

        Console.WriteLine($"FFmpeg set: {BundledFfmpegPath}");
        return 0;
    }

    /// <summary>Ensures ffmpeg is available, downloading it if necessary.</summary>
    public static async Task<string> EnsureAsync()
    {
        var existing = Locate();
        if (existing is not null)
            return existing;

        Directory.CreateDirectory(AppDataDir);

        Console.WriteLine("FFmpeg not found. Downloading static build (once).");
        Console.WriteLine($"  Source: {DownloadUrl}");
        Console.WriteLine($"  Target: {BundledFfmpegPath}");

        var tempZip = Path.Combine(AppDataDir, "ffmpeg-download.zip");
        var tempExtract = Path.Combine(AppDataDir, "ffmpeg-extract");

        try
        {
            await DownloadWithParallelChunksAsync(DownloadUrl, tempZip);

            if (Directory.Exists(tempExtract))
                Directory.Delete(tempExtract, recursive: true);
            Directory.CreateDirectory(tempExtract);

            Console.WriteLine("  Extracting...");
            ZipFile.ExtractToDirectory(tempZip, tempExtract);

            var ffmpegExe = Directory
                .EnumerateFiles(tempExtract, "ffmpeg.exe", SearchOption.AllDirectories)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("ffmpeg.exe not found inside the archive.");

            File.Copy(ffmpegExe, BundledFfmpegPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }

        Console.WriteLine("  FFmpeg ready.");
        return BundledFfmpegPath;
    }

    // ----------------------------------------------------------------------
    // Download
    // ----------------------------------------------------------------------

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = ParallelChunks + 2,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WCC/1.0 (+https://local)");
        // Some servers are picky without Accept.
        http.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream, */*");
        return http;
    }

    private static async Task DownloadWithParallelChunksAsync(string url, string destPath)
    {
        using var http = CreateHttpClient();

        // Probe: request bytes=0-0 to discover total size + range support.
        // 206 Partial Content -> ranges work, Content-Range gives total.
        // 200 OK              -> ranges ignored, fall back to single stream.
        long total;
        bool rangesSupported;

        using (var probeReq = new HttpRequestMessage(HttpMethod.Get, url))
        {
            probeReq.Headers.Range = new RangeHeaderValue(0, 0);
            using var probeResp = await http.SendAsync(probeReq, HttpCompletionOption.ResponseHeadersRead);

            if (probeResp.StatusCode == HttpStatusCode.PartialContent &&
                probeResp.Content.Headers.ContentRange is { HasLength: true, Length: > 0 } cr)
            {
                total = cr.Length!.Value;
                rangesSupported = true;
            }
            else
            {
                probeResp.EnsureSuccessStatusCode();
                total = probeResp.Content.Headers.ContentLength ?? -1;
                rangesSupported = false;
            }
        }

        if (!rangesSupported || total <= 0 || ParallelChunks <= 1)
        {
            await DownloadSingleStreamAsync(http, url, destPath, total);
            return;
        }

        // Pre-allocate the output file.
        await using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.Write))
        {
            fs.SetLength(total);
        }

        long bytesDone = 0;
        var startTime = DateTime.UtcNow;
        using var progressCts = new CancellationTokenSource();
        var progressTask = Task.Run(() => ProgressLoop(() => Volatile.Read(ref bytesDone), total, startTime, progressCts.Token));

        // Split into N roughly-equal chunks.
        var chunks = new List<(long Start, long End)>();
        long chunkSize = total / ParallelChunks;
        for (int i = 0; i < ParallelChunks; i++)
        {
            long start = i * chunkSize;
            long end = (i == ParallelChunks - 1) ? total - 1 : start + chunkSize - 1;
            chunks.Add((start, end));
        }

        var tasks = chunks.Select(c => DownloadChunkAsync(http, url, destPath, c.Start, c.End, n => Interlocked.Add(ref bytesDone, n)))
                          .ToArray();

        try
        {
            await Task.WhenAll(tasks);
        }
        finally
        {
            progressCts.Cancel();
            try { await progressTask; } catch { }
            Console.WriteLine();
        }
    }

    private static async Task DownloadChunkAsync(
        HttpClient http, string url, string destPath,
        long start, long end, Action<int> onBytes)
    {
        // Retry a couple of times on transient errors - CDNs occasionally 503.
        const int maxAttempts = 3;
        long localStart = start;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new RangeHeaderValue(localStart, end);

                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();

                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var fs = new FileStream(
                    destPath, FileMode.Open, FileAccess.Write, FileShare.Write,
                    bufferSize: 81920, useAsync: true);
                fs.Position = localStart;

                var buffer = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buffer)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, n));
                    onBytes(n);
                    localStart += n;
                }
                return;
            }
            catch (Exception) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt));
                // resume from where we left off
            }
        }
    }

    private static async Task DownloadSingleStreamAsync(HttpClient http, string url, string destPath, long totalHint)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? totalHint;
        long bytesDone = 0;
        var startTime = DateTime.UtcNow;
        using var progressCts = new CancellationTokenSource();
        var progressTask = Task.Run(() => ProgressLoop(() => Volatile.Read(ref bytesDone), total, startTime, progressCts.Token));

        try
        {
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var fs = File.Create(destPath);
            var buf = new byte[81920];
            int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n));
                Interlocked.Add(ref bytesDone, n);
            }
        }
        finally
        {
            progressCts.Cancel();
            try { await progressTask; } catch { }
            Console.WriteLine();
        }
    }

    // ----------------------------------------------------------------------
    // Progress
    // ----------------------------------------------------------------------

    private static async Task ProgressLoop(Func<long> getDone, long total, DateTime startUtc, CancellationToken ct)
    {
        long lastBytes = 0;
        var lastTick = DateTime.UtcNow;
        // Exponential moving average of bytes/second.
        double emaBps = 0;

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(250, CancellationToken.None);
            var now = DateTime.UtcNow;
            long done = getDone();
            double dt = (now - lastTick).TotalSeconds;
            if (dt <= 0) dt = 0.001;
            double instBps = (done - lastBytes) / dt;
            emaBps = emaBps == 0 ? instBps : (emaBps * 0.7 + instBps * 0.3);
            lastBytes = done;
            lastTick = now;

            WriteProgress(done, total, emaBps);
            if (ct.IsCancellationRequested) break;
        }

        // final line
        WriteProgress(getDone(), total, emaBps);
    }

    private static void WriteProgress(long done, long total, double bps)
    {
        string speed = FormatBytes((long)bps) + "/s";
        string doneStr = FormatBytes(done);

        string line;
        if (total > 0)
        {
            double pct = 100.0 * done / total;
            string totalStr = FormatBytes(total);
            string eta;
            if (bps > 1)
            {
                var remaining = TimeSpan.FromSeconds((total - done) / bps);
                eta = FormatEta(remaining);
            }
            else eta = "--:--";

            // Simple text bar.
            const int barWidth = 24;
            int filled = (int)Math.Clamp(Math.Round(pct / 100.0 * barWidth), 0, barWidth);
            string bar = new string('#', filled) + new string('-', barWidth - filled);

            line = $"  [{bar}] {pct,5:0.0}%  {doneStr}/{totalStr}  {speed,10}  ETA {eta}";
        }
        else
        {
            line = $"  {doneStr}  {speed,10}";
        }

        // Trim to console width and pad so we always overwrite previous line fully.
        int width = 100;
        try { width = Math.Max(40, Console.BufferWidth - 1); } catch { }
        if (line.Length > width) line = line[..width];
        else line = line.PadRight(width);
        Console.Write("\r" + line);
    }

    private static string FormatBytes(long b)
    {
        if (b < 1024) return $"{b} B";
        double v = b;
        string[] units = { "KB", "MB", "GB", "TB" };
        int ui = -1;
        do { v /= 1024; ui++; } while (v >= 1024 && ui < units.Length - 1);
        return $"{v,6:0.00} {units[ui]}";
    }

    private static string FormatEta(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        return $"{t.Minutes:00}:{t.Seconds:00}";
    }

    // ----------------------------------------------------------------------
    // PATH lookup
    // ----------------------------------------------------------------------

    private static string? FindOnPath(string exeName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { /* ignore malformed PATH segments */ }
        }
        return null;
    }
}
