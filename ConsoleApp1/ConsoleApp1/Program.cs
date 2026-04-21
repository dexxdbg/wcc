using System.Diagnostics;

namespace Wcc;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Unhandled error: " + ex);
            Console.Error.WriteLine();
            Console.Error.WriteLine("Press any key to close...");
            try { Console.ReadKey(intercept: true); } catch { }
            return 99;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        var cmd = args[0].ToLowerInvariant();
        switch (cmd)
        {
            case "install":
                return ContextMenu.Install(CurrentExePath());

            case "uninstall":
                return ContextMenu.Uninstall();

            case "ensure-ffmpeg":
            {
                var existing = FFmpegManager.Locate();
                if (existing is not null)
                {
                    Console.WriteLine($"Already installed, skipping. ({existing})");
                    return 0;
                }
                await FFmpegManager.EnsureAsync();
                return 0;
            }

            case "set-ffmpeg":
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("Usage: wcc set-ffmpeg <path-to-ffmpeg.exe>");
                    return 1;
                }
                return FFmpegManager.SetFromPath(args[1]);

            case "formats":
            case "list":
                PrintFormats();
                return 0;

            case "convert":
                if (args.Length < 3)
                {
                    Console.Error.WriteLine("Usage: wcc convert <input> <target-format>");
                    return 1;
                }
                return await Converter.ConvertAsync(args[1], args[2]);

            case "-h":
            case "--help":
            case "help":
                PrintUsage();
                return 0;

            default:
                Console.Error.WriteLine($"Unknown command: {cmd}");
                PrintUsage();
                return 1;
        }
    }

    private static string CurrentExePath()
    {
        // Process.MainModule.FileName gives the real .exe even when published single-file.
        var p = Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(p)) return p!;
        return Environment.ProcessPath ?? AppContext.BaseDirectory;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("WCC - Windows Context Converter");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  wcc install                    Register 'Convert with WCC' in Explorer context menu");
        Console.WriteLine("  wcc uninstall                  Remove the context menu entries");
        Console.WriteLine("  wcc ensure-ffmpeg              Download FFmpeg now (otherwise done on first convert)");
        Console.WriteLine("  wcc set-ffmpeg <path>          Use an existing ffmpeg.exe (skips the download)");
        Console.WriteLine("  wcc formats                    List all supported target formats");
        Console.WriteLine("  wcc convert <input> <format>   Convert a file (format = any id from 'wcc formats')");
        Console.WriteLine();
        Console.WriteLine("After 'install', right-click any supported media file in Explorer.");
        Console.WriteLine("On Windows 11 the menu lives under 'Show more options'.");
    }

    private static void PrintFormats()
    {
        Console.WriteLine("Target formats:");
        Console.WriteLine();
        Console.WriteLine("  Video:");
        foreach (var t in Formats.Targets.Where(t => t.Kind == FormatKind.Video))
            Console.WriteLine($"    {t.Id,-6}  {t.DisplayName}");
        Console.WriteLine();
        Console.WriteLine("  Audio:");
        foreach (var t in Formats.Targets.Where(t => t.Kind == FormatKind.Audio))
            Console.WriteLine($"    {t.Id,-6}  {t.DisplayName}");
        Console.WriteLine();
        Console.WriteLine("Source extensions recognised by the context menu:");
        Console.WriteLine("  Video: " + string.Join(" ", Formats.VideoExtensions));
        Console.WriteLine("  Audio: " + string.Join(" ", Formats.AudioExtensions));
    }
}
