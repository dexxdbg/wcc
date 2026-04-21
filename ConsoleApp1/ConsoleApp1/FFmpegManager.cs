using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Wcc;

// handles finding, downloading, and setting up ffmpeg
// we dont bundle ffmpeg in the exe because it would be like 100MB
// instead we download it once to %LOCALAPPDATA%\WCC and reuse it forever
internal static class FFmpegManager
{
    // BtbN maintains a rolling "latest" release on github that gets updated automatically
    // its way faster than gyan.dev which was what we used before - github CDN is just better
    // GPL build includes basically every codec we need (x264, vp9, opus, etc)
    private const string DownloadUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    // how many parallel chunks to use when downloading
    // most CDNs throttle per connection so 8 parallel ones usually saturates home connections
    private const int ParallelChunks = 8;

    // where we store ffmpeg and any temp files during download
    public static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WCC");

    // the actual path we expect ffmpeg to be at after download/install
    public static string BundledFfmpegPath => Path.Combine(AppDataDir, "ffmpeg.exe");

    // checks if we have a usable ffmpeg anywhere - bundled first, then PATH
    public static string? Locate()
    {
        if (File.Exists(BundledFfmpegPath))
            return BundledFfmpegPath;

        // fallback: maybe the user already has ffmpeg installed system-wide
        var onPath = FindOnPath("ffmpeg.exe");
        return onPath;
    }

    // lets the user point us at an ffmpeg they already have instead of downloading
    // just copies it into our appdata dir so Locate() picks it up next time
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

        // make sure they didnt accidentally point at some random exe
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

    // main entry point - makes sure ffmpeg exists, downloading if needed
    // called automatically before every conversion so users dont have to think about it
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

            // clean up any leftover extract folder from a previous failed attempt
            if (Directory.Exists(tempExtract))
                Directory.Delete(tempExtract, recursive: true);
            Directory.CreateDirectory(tempExtract);

            Console.WriteLine("  Extracting...");
            ZipFile.ExtractToDirectory(tempZip, tempExtract);

            // the zip has a top level folder like "ffmpeg-7.x-build", ffmpeg.exe is inside bin/
            var ffmpegExe = Directory
                .EnumerateFiles(tempExtract, "ffmpeg.exe", SearchOption.AllDirectories)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("ffmpeg.exe not found inside the archive.");

            File.Copy(ffmpegExe, BundledFfmpegPath, overwrite: true);
        }
        finally
        {
            // always clean up temp files even if something went wrong
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }

        Console.WriteLine("  FFmpeg ready.");
        return BundledFfmpegPath;
    }

    // -------------------------------------------------------------------------
    // Download stuff
    // -------------------------------------------------------------------------

    // creates an http client configured for parallel chunk downloads
    // need extra connections per server since we're making 8 at once
    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = ParallelChunks + 2,
            AutomaticDecompression = DecompressionMethods.None, // file is already a zip, dont double-decompress
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WCC/1.0 (+https://local)");
        // some servers reject requests without an Accept header
        http.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream, */*");
        return http;
    }

    // downloads the file using multiple parallel range requests for speed
    // first probes if the server supports range requests, falls back to single stream if not
    private static async Task DownloadWithParallelChunksAsync(string url, string destPath)
    {
        using var http = CreateHttpClient();

        // send a range request for just the first byte to check:
        // - if the server supports partial content (206 = yes, 200 = no)
        // - what the total file size is (from Content-Range header)
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

        // if range requests arent supported just fall back to normal download
        if (!rangesSupported || total <= 0 || ParallelChunks <= 1)
        {
            await DownloadSingleStreamAsync(http, url, destPath, total);
            return;
        }

        // pre-allocate the full file so all chunks can write to their correct offset in parallel
        await using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.Write))
        {
            fs.SetLength(total);
        }

        long bytesDone = 0;
        var startTime = DateTime.UtcNow;
        using var progressCts = new CancellationTokenSource();

        // progress runs on a background task so it doesnt block the downloads
        var progressTask = Task.Run(() => ProgressLoop(() => Volatile.Read(ref bytesDone), total, startTime, progressCts.Token));

        // split the file into N equal chunks and download them all at once
        var chunks = new List<(long Start, long End)>();
        long chunkSize = total / ParallelChunks;
        for (int i = 0; i < ParallelChunks; i++)
        {
            long start = i * chunkSize;
            // last chunk gets any leftover bytes from integer division
            long end = (i == ParallelChunks - 1) ? total - 1 : start + chunkSize - 1;
            chunks.Add((start, end));
        }

        var tasks = chunks
            .Select(c => DownloadChunkAsync(http, url, destPath, c.Start, c.End, n => Interlocked.Add(ref bytesDone, n)))
            .ToArray();

        try
        {
            await Task.WhenAll(tasks);
        }
        finally
        {
            progressCts.Cancel();
            try { await progressTask; } catch { }
            Console.WriteLine(); // newline after the progress line
        }
    }

    // downloads a single byte range and writes it directly to the right offset in the output file
    // retries a couple times on failure since CDNs occasionally throw 503s
    private static async Task DownloadChunkAsync(
        HttpClient http, string url, string destPath,
        long start, long end, Action<int> onBytes)
    {
        const int maxAttempts = 3;
        long localStart = start; // tracks progress within this chunk so we can resume on retry

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
                    localStart += n; // advance so if we retry we dont re-download bytes we already have
                }
                return;
            }
            catch (Exception) when (attempt < maxAttempts)
            {
                // wait a bit before retrying - exponential backoff
                await Task.Delay(TimeSpan.FromSeconds(attempt));
            }
        }
    }

    // fallback single-stream download for servers that dont support range requests
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

    // -------------------------------------------------------------------------
    // Progress display
    // -------------------------------------------------------------------------

    // runs in a background loop every 250ms, reads the shared bytesDone counter
    // and prints a progress line with speed and ETA
    private static async Task ProgressLoop(Func<long> getDone, long total, DateTime startUtc, CancellationToken ct)
    {
        long lastBytes = 0;
        var lastTick = DateTime.UtcNow;
        // use an exponential moving average for speed so it doesnt jump around too much
        double emaBps = 0;

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(250, CancellationToken.None);
            var now = DateTime.UtcNow;
            long done = getDone();
            double dt = (now - lastTick).TotalSeconds;
            if (dt <= 0) dt = 0.001; // avoid divide by zero on first tick
            double instBps = (done - lastBytes) / dt;
            // 70% old value + 30% new value - smooths out spikes
            emaBps = emaBps == 0 ? instBps : (emaBps * 0.7 + instBps * 0.3);
            lastBytes = done;
            lastTick = now;

            WriteProgress(done, total, emaBps);
            if (ct.IsCancellationRequested) break;
        }

        // one final update when we're done
        WriteProgress(getDone(), total, emaBps);
    }

    // writes the actual progress line to the console, overwriting the previous one with \r
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

            // simple ascii progress bar made of # and -
            const int barWidth = 24;
            int filled = (int)Math.Clamp(Math.Round(pct / 100.0 * barWidth), 0, barWidth);
            string bar = new string('#', filled) + new string('-', barWidth - filled);

            line = $"  [{bar}] {pct,5:0.0}%  {doneStr}/{totalStr}  {speed,10}  ETA {eta}";
        }
        else
        {
            // no total size known, just show bytes and speed
            line = $"  {doneStr}  {speed,10}";
        }

        // pad to console width so previous longer lines get fully overwritten
        int width = 100;
        try { width = Math.Max(40, Console.BufferWidth - 1); } catch { }
        if (line.Length > width) line = line[..width];
        else line = line.PadRight(width);
        Console.Write("\r" + line);
    }

    // converts bytes to a human readable string (KB, MB, etc)
    private static string FormatBytes(long b)
    {
        if (b < 1024) return $"{b} B";
        double v = b;
        string[] units = { "KB", "MB", "GB", "TB" };
        int ui = -1;
        do { v /= 1024; ui++; } while (v >= 1024 && ui < units.Length - 1);
        return $"{v,6:0.00} {units[ui]}";
    }

    // formats a timespan as MM:SS or HH:MM:SS depending on how long it is
    private static string FormatEta(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        return $"{t.Minutes:00}:{t.Seconds:00}";
    }

    // -------------------------------------------------------------------------
    // PATH lookup
    // -------------------------------------------------------------------------

    // searches the system PATH for an exe, same way the shell does it
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
            catch { /* some PATH entries are malformed, just skip them */ }
        }
        return null;
    }
}
