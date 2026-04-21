using System.Diagnostics;

namespace Wcc;

// this is the entry point, basically just reads the first arg and calls the right thing
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
            // if something completely unexpected blows up, show it and wait
            // so the console doesnt just vanish instantly
            Console.Error.WriteLine("Unhandled error: " + ex);
            Console.Error.WriteLine();
            Console.Error.WriteLine("Press any key to close...");
            try { Console.ReadKey(intercept: true); } catch { }
            return 99;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        // no args = just print help, dont crash
        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        var cmd = args[0].ToLowerInvariant();
        switch (cmd)
        {
            // registers the right-click menu entries in the registry
            case "install":
                return ContextMenu.Install(CurrentExePath());

            // cleans up everything we added to the registry
            case "uninstall":
                return ContextMenu.Uninstall();

            // lets user pre-download ffmpeg so it doesnt happen mid-conversion
            case "ensure-ffmpeg":
            {
                var existing = FFmpegManager.Locate();
                if (existing is not null)
                {
                    // already have it, no point downloading again
                    Console.WriteLine($"Already installed, skipping. ({existing})");
                    return 0;
                }
                await FFmpegManager.EnsureAsync();
                return 0;
            }

            // if the user already has ffmpeg somewhere they can just point us at it
            // saves downloading the whole thing
            case "set-ffmpeg":
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("Usage: wcc set-ffmpeg <path-to-ffmpeg.exe>");
                    return 1;
                }
                return FFmpegManager.SetFromPath(args[1]);

            // just lists all the formats we support, useful to know what to type in convert
            case "formats":
            case "list":
                PrintFormats();
                return 0;

            // this is what the context menu actually calls when you click a format
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

    // gets the real path of this exe even when published as a single file
    // Process.MainModule is more reliable than Assembly.Location in that case
    private static string CurrentExePath()
    {
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
