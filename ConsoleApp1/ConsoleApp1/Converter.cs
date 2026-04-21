using System.Diagnostics;

namespace Wcc;

// this is what actually runs ffmpeg and does the conversion
// gets called by Program.cs when the user clicks a format in the right-click menu
internal static class Converter
{
    public static async Task<int> ConvertAsync(string inputPath, string targetId)
    {
        // basic sanity checks before we even bother launching ffmpeg
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

        // this will download ffmpeg if we dont have it yet
        var ffmpeg = await FFmpegManager.EnsureAsync();

        // output goes next to the input file with the new extension
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
            // keep stderr/stdout open so ffmpeg progress shows in the console window
            RedirectStandardError = false,
            RedirectStandardOutput = false,
            CreateNoWindow = false
        };

        // -hide_banner cuts down the noisy version/build info ffmpeg prints at the start
        // -y means overwrite output if it exists (shouldnt happen due to UniquePath but just in case)
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputPath);

        // add the codec/quality args from the format preset
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
            // non-zero exit usually means ffmpeg printed an error above already
            Console.WriteLine($"FFmpeg exited with code {proc.ExitCode}.");
        }

        // wait for a keypress before closing so the user can actually read the output
        // especially important when launched from the context menu since theres no persistent terminal
        Console.WriteLine();
        Console.WriteLine("Press any key to close...");
        try { Console.ReadKey(intercept: true); } catch { /* no console attached in some edge cases */ }

        return proc.ExitCode;
    }

    // if the output path already exists, add (1), (2), etc before the extension
    // so we never silently overwrite something the user might want to keep
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
        // if somehow all 9999 slots are taken, just let ffmpeg overwrite with -y
        return path;
    }

    // splits a preset arg string like "-c:v libx264 -crf 20" into individual tokens
    // we dont support quoted args in presets so plain split on space is fine
    private static IEnumerable<string> SplitArgs(string s) =>
        s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
