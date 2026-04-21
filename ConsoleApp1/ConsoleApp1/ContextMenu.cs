using Microsoft.Win32;

namespace Wcc;

/// <summary>
/// Installs/uninstalls a cascading "Convert with WCC" context-menu entry for
/// recognised video and audio file extensions. Uses HKCU so no admin required.
///
/// Layout per extension:
///   HKCU\Software\Classes\SystemFileAssociations\.ext\shell\WCC
///       MUIVerb     = "Convert with WCC"
///       subcommands = ""                (empty -> cascade enabled)
///       Icon        = "<exe>,0"
///       shell\
///           01_mp4\
///               MUIVerb = "To MP4"
///               command\(default) = "<exe>" convert "%1" mp4
///           02_mkv\ ...
/// </summary>
internal static class ContextMenu
{
    private const string RootKey = "WCC";
    private const string ParentMenuLabel = "Convert with WCC";

    private static string BuildBase(string ext) =>
        $@"Software\Classes\SystemFileAssociations\{ext}\shell\{RootKey}";

    public static int Install(string exePath)
    {
        exePath = Path.GetFullPath(exePath);
        if (!File.Exists(exePath))
        {
            Console.Error.WriteLine($"Executable not found: {exePath}");
            return 1;
        }

        var allExts = Formats.VideoExtensions.Concat(Formats.AudioExtensions).ToArray();
        int touched = 0;

        foreach (var ext in allExts)
        {
            var targets = Formats.TargetsFor(ext).ToArray();
            if (targets.Length == 0) continue;

            WriteExtensionMenu(ext, exePath, targets);
            touched++;
        }

        Console.WriteLine($"Installed context menu for {touched} extension(s).");
        Console.WriteLine("Note: on Windows 11 the menu appears under 'Show more options' (Shift+F10).");
        return 0;
    }

    public static int Uninstall()
    {
        var allExts = Formats.VideoExtensions.Concat(Formats.AudioExtensions).ToArray();
        int removed = 0;

        foreach (var ext in allExts)
        {
            var path = BuildBase(ext);
            try
            {
                using var parent = Registry.CurrentUser.OpenSubKey(
                    $@"Software\Classes\SystemFileAssociations\{ext}\shell", writable: true);
                if (parent is null) continue;
                if (parent.GetSubKeyNames().Contains(RootKey))
                {
                    parent.DeleteSubKeyTree(RootKey, throwOnMissingSubKey: false);
                    removed++;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  failed for {ext}: {ex.Message}");
            }
        }

        Console.WriteLine($"Removed context menu from {removed} extension(s).");
        return 0;
    }

    private static void WriteExtensionMenu(string ext, string exePath, TargetFormat[] targets)
    {
        var basePath = BuildBase(ext);

        // IMPORTANT: wipe any existing subtree first, otherwise a re-install with
        // a different format list leaves stale sub-items behind (causing apparent
        // duplicates in the cascade menu).
        try
        {
            using var parentShell = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\SystemFileAssociations\{ext}\shell", writable: true);
            if (parentShell is not null && parentShell.GetSubKeyNames().Contains(RootKey))
                parentShell.DeleteSubKeyTree(RootKey, throwOnMissingSubKey: false);
        }
        catch { /* non-fatal - we will overwrite values below */ }

        // Parent cascade key.
        using (var parent = Registry.CurrentUser.CreateSubKey(basePath, writable: true))
        {
            parent.SetValue("MUIVerb", ParentMenuLabel, RegistryValueKind.String);
            parent.SetValue("subcommands", "", RegistryValueKind.String); // empty -> cascade
            parent.SetValue("Icon", $"\"{exePath}\",0", RegistryValueKind.String);
        }

        // Sub-items live under parent\shell\<ordered_name>.
        for (int i = 0; i < targets.Length; i++)
        {
            var t = targets[i];
            // Prefix with an index so the order matches our Formats.Targets array.
            var subKeyName = $"{i + 1:00}_{t.Id}";
            var subKeyPath = $@"{basePath}\shell\{subKeyName}";

            using var sub = Registry.CurrentUser.CreateSubKey(subKeyPath, writable: true);
            sub.SetValue("MUIVerb", t.DisplayName, RegistryValueKind.String);

            using var cmd = sub.CreateSubKey("command", writable: true);
            // "%1" is the clicked file. Quote everything to handle spaces.
            var command = $"\"{exePath}\" convert \"%1\" {t.Id}";
            cmd.SetValue("", command, RegistryValueKind.String);
        }
    }
}
