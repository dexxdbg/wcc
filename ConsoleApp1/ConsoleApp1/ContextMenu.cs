using Microsoft.Win32;

namespace Wcc;

// handles writing and removing the right-click menu entries in the registry
// everything goes into HKCU (current user) so we dont need admin rights
//
// the registry layout per extension looks like this:
//   HKCU\Software\Classes\SystemFileAssociations\.mp4\shell\WCC
//       MUIVerb     = "Convert with WCC"    <- the parent menu label
//       subcommands = ""                    <- empty string = enable cascade submenu
//       Icon        = "C:\...\wcc.exe,0"   <- icon from first resource in the exe
//       shell\
//           01_mp4\   <- numbered prefix keeps them in a predictable order
//               MUIVerb = "To MP4"
//               command\(default) = "C:\...\wcc.exe" convert "%1" mp4
//           02_mkv\
//               ...
internal static class ContextMenu
{
    private const string RootKey = "WCC";
    private const string ParentMenuLabel = "Convert with WCC";

    // builds the registry path for a given extension
    private static string BuildBase(string ext) =>
        $@"Software\Classes\SystemFileAssociations\{ext}\shell\{RootKey}";

    // installs the context menu for every supported extension
    // no admin needed, HKCU is per-user
    public static int Install(string exePath)
    {
        exePath = Path.GetFullPath(exePath);
        if (!File.Exists(exePath))
        {
            Console.Error.WriteLine($"Executable not found: {exePath}");
            return 1;
        }

        var allExts = Formats.VideoExtensions.Concat(Formats.AudioExtensions).Concat(Formats.ImageExtensions).ToArray();
        int touched = 0;

        foreach (var ext in allExts)
        {
            var targets = Formats.TargetsFor(ext).ToArray();
            if (targets.Length == 0) continue; // skip if no targets apply (shouldnt happen but just in case)

            WriteExtensionMenu(ext, exePath, targets);
            touched++;
        }

        Console.WriteLine($"Installed context menu for {touched} extension(s).");
        Console.WriteLine("Note: on Windows 11 the menu appears under 'Show more options' (Shift+F10).");
        return 0;
    }

    // removes all the WCC subkeys we added - clean uninstall
    public static int Uninstall()
    {
        var allExts = Formats.VideoExtensions.Concat(Formats.AudioExtensions).Concat(Formats.ImageExtensions).ToArray();
        int removed = 0;

        foreach (var ext in allExts)
        {
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

    // writes the cascade menu for a single extension
    private static void WriteExtensionMenu(string ext, string exePath, TargetFormat[] targets)
    {
        var basePath = BuildBase(ext);

        // always nuke the existing WCC subtree before writing
        // this is important - if you reinstall after adding/removing formats,
        // old subkeys with different indexes would stick around and cause duplicates
        try
        {
            using var parentShell = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\SystemFileAssociations\{ext}\shell", writable: true);
            if (parentShell is not null && parentShell.GetSubKeyNames().Contains(RootKey))
                parentShell.DeleteSubKeyTree(RootKey, throwOnMissingSubKey: false);
        }
        catch { /* not fatal, worst case we just overwrite the values */ }

        // write the parent menu key that shows "Convert with WCC"
        using (var parent = Registry.CurrentUser.CreateSubKey(basePath, writable: true))
        {
            parent.SetValue("MUIVerb", ParentMenuLabel, RegistryValueKind.String);
            parent.SetValue("subcommands", "", RegistryValueKind.String); // empty = show submenu
            parent.SetValue("Icon", $"\"{exePath}\",0", RegistryValueKind.String);
        }

        // write one subkey per target format
        // the numeric prefix keeps them sorted in the order we defined in Formats.cs
        for (int i = 0; i < targets.Length; i++)
        {
            var t = targets[i];
            var subKeyName = $"{i + 1:00}_{t.Id}"; // e.g. "01_mp4", "02_mkv"
            var subKeyPath = $@"{basePath}\shell\{subKeyName}";

            using var sub = Registry.CurrentUser.CreateSubKey(subKeyPath, writable: true);
            sub.SetValue("MUIVerb", t.DisplayName, RegistryValueKind.String);

            // the command that runs when the user clicks the item
            // %1 is the full path to the file they right-clicked
            using var cmd = sub.CreateSubKey("command", writable: true);
            var command = $"\"{exePath}\" convert \"%1\" {t.Id}";
            cmd.SetValue("", command, RegistryValueKind.String);
        }
    }
}
