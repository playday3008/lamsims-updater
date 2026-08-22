using LamSims.Core.Settings;

namespace LamSims.Core.Tests;

public class AppPathsTests
{
    // The expected path is spelled out rather than reusing AppPaths' own expression: on Unix,
    // ApplicationData follows XDG_CONFIG_HOME when set and falls back to ~/.config.
    [Fact]
    public void Defaults_under_the_users_config_directory()
    {
        var paths = new AppPaths();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

        var expected = OperatingSystem.IsWindows()
            ? Path.Combine(home, "AppData", "Roaming", "lamsims-updater")
            : Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg,
                           "lamsims-updater");

        Assert.Equal(expected, paths.Root);
    }

    [Fact]
    public void Names_the_settings_and_cache_files_inside_the_root()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);

        Assert.Equal(Path.Combine(temp.Path, "settings.json"), paths.SettingsFile);
        Assert.Equal(Path.Combine(temp.Path, "catalog.cache.json"), paths.CatalogCacheFile);
    }

    [Fact]
    public void Creates_the_root_on_demand()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(Path.Combine(temp.Path, "nested", "root"));

        paths.EnsureCreated();

        Assert.True(Directory.Exists(paths.Root));
    }

    [Fact]
    public void Names_the_install_state_directory_inside_the_root()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);

        Assert.Equal(Path.Combine(temp.Path, "installs"), paths.InstallStateDirectory);
    }
}
