namespace ParkedTraffic;

/// <summary>
/// Where settings live: %LOCALAPPDATA%\ParkedTraffic\config, whatever folder the exe is run from, so moving or
/// re-downloading the exe keeps your quotas and rules. Config found next to the exe (older versions) is copied
/// over once; the originals are left where they are.
/// </summary>
public static class ConfigDir
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ParkedTraffic", "config");

    private static bool _migrated;

    /// <summary>Full path for a config file, e.g. "EGLL.json". Creates the folder and migrates old config on first use.</summary>
    public static string File(string name)
    {
        if (!_migrated)
        {
            _migrated = true;
            Directory.CreateDirectory(Path);
            MigrateFromExeFolder();
        }
        return System.IO.Path.Combine(Path, name);
    }

    private static void MigrateFromExeFolder()
    {
        try
        {
            var old = System.IO.Path.Combine(AppContext.BaseDirectory, "config");
            if (!Directory.Exists(old) || System.IO.Path.GetFullPath(old).Equals(System.IO.Path.GetFullPath(Path), StringComparison.OrdinalIgnoreCase))
                return;
            int copied = 0;
            foreach (var src in Directory.EnumerateFiles(old, "*.json"))
            {
                var dest = System.IO.Path.Combine(Path, System.IO.Path.GetFileName(src));
                if (System.IO.File.Exists(dest)) continue; // never overwrite settings already in the user folder
                System.IO.File.Copy(src, dest);
                copied++;
            }
            if (copied > 0) Log.Write($"Copied {copied} config file(s) from {old} to {Path}.");
        }
        catch (Exception e) { Log.Write($"Could not copy old config ({e.Message})."); }
    }
}
