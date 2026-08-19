namespace LamSims.Core.Settings;

/// <summary>
/// Where the application keeps state that is not a download: settings and the cached
/// catalog. <see cref="Environment.SpecialFolder.ApplicationData"/> resolves to
/// <c>~/.config</c> on Linux and macOS and <c>%APPDATA%</c> on Windows, so one call
/// covers all three.
///
/// <see cref="Environment.SpecialFolderOption.DoNotVerify"/> because the default option answers
/// with an empty string for a directory that does not exist yet, which is what a Linux account
/// with no ~/.config has. <see cref="Path.Combine(string, string)"/> turns that empty string into
/// a relative path, and every file below would land in the process's working directory.
/// </summary>
public sealed class AppPaths
{
    public string Root { get; }

    public AppPaths(string? overrideRoot = null)
    {
        Root = overrideRoot ?? Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.DoNotVerify),
            "lamsims-updater");
    }

    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string CatalogCacheFile => Path.Combine(Root, "catalog.cache.json");

    /// <summary>
    /// Where per-pack install markers live, grouped by game directory. Under the app's own
    /// root rather than inside the game directory: the game directory belongs to EA's
    /// installer, repair tools, mod managers and the user, all of which routinely remove
    /// entries they do not recognise.
    /// </summary>
    public string InstallStateDirectory => Path.Combine(Root, "installs");

    /// <summary>
    /// Where the EADM autostart value removed at install time is recorded, so removal can put it
    /// back. Under the app's own root and not in the unlocker's config directory, because removal
    /// deletes that directory wholesale and a backup stored there could not survive the operation
    /// that needs to read it.
    /// </summary>
    public string UnlockerAutostartBackupFile => Path.Combine(Root, "unlocker-autostart.json");

    public void EnsureCreated() => Directory.CreateDirectory(Root);
}
