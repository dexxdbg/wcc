using System.Diagnostics;

namespace Wcc;

/// <summary>
/// Runs ffmpeg to convert a single input file to the selected target format.
/// </summary>
internal static class Converter
{
    public static async Task<int> ConvertAsync(string inputPath, string targetId)
    {
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Input file not found: {inputPath}");
            return 2;
        }

        var target = Formats.FindById(targetId);
        if (target is null)
        {
            Console.Error.WriteLine($"Unknown target format '{targetId}'.");
            return 3;
        }

        var ffmpeg = await FFmpegManager.EnsureAsync();

        var dir = Path.GetDirectoryName(Path.GetFullPath(inputPath))!;
        var baseName = Path.GetFileNameWithoutExtension(inputPath);
        var outputPath = UniquePath(Path.Combine(dir, $"{baseName}.{target.Id}"));

        Console.WriteLine();
        Console.WriteLine($"  Input : {inputPath}");
        Console.WriteLine($"  Output: {outputPath}");
        Console.WriteLine($"  Format: {target.DisplayName}");
        Console.WriteLine();

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardError = false,
            RedirectStandardOutput = false,
            CreateNoWindow = false
        };
        // -y = overwrite (shouldn't happen since we picked a unique name, but safe).
        // -stats = show progress line on stderr.
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputPath);
        foreach (var a in SplitArgs(target.FfmpegArgs))
            psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(outputPath);

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            Console.Error.WriteLine("Failed to start ffmpeg.");
            return 4;
        }

        await proc.WaitForExitAsync();

        Console.WriteLine();
        if (proc.ExitCode == 0)
        {
            Console.WriteLine("Done.");
        }
        else
        {
            Console.WriteLine($"FFmpeg exited with code {proc.ExitCode}.");
        }

        // Pause so the user can read the result before the console closes.
        Console.WriteLine();
        Console.WriteLine("Press any key to close...");
        try { Console.ReadKey(intercept: true); } catch { /* no console in some launches */ }

        return proc.ExitCode;
    }

    /// <summary>If <paramref name="path"/> exists, append " (1)", " (2)", ... before the extension.</summary>
    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return path; // give up, caller will deal with overwrite via -y.
    }

    /// <summary>Very small whitespace-aware argument splitter (no quoting in our preset strings).</summary>
    private static IEnumerable<string> SplitArgs(string s) =>
        s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
