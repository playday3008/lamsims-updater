using System;
using System.IO;
using Xunit;
using LamSims.Core.Settings;

namespace LamSims.Core.Tests;

public class AppPathsTests
{
    // Three assertions, because only the last of them can be strong. Rooted comes first and holds
    // on every platform. The suffix is checked next against the same call AppPaths makes, which
    // cannot catch a wrong DIRECTORY but does catch a Root that forgot to name the application and
    // would drop settings straight into the user's configuration directory. Where the platform's
    // answer is a documented constant — Windows and Linux — it is then spelled out independently,
    // which is what pins the directory itself. macOS is deliberately left to the first two: .NET's
    // answer there is not a constant this test should be asserting from memory.
    //
    // Rooted is separate from the suffix rather than implied by it. Asked without DoNotVerify,
    // GetFolderPath answers with an empty string for a configuration directory that does not exist
    // yet, and a suffix comparison drawn from that same empty answer agrees with a relative Root.
    [Fact]
    public void Defaults_under_the_users_config_directory()
    {
        var paths = new AppPaths();

        Assert.True(Path.IsPathRooted(paths.Root), $"'{paths.Root}' is not an absolute path");

        var appData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify);

        Assert.Equal(Path.Combine(appData, "lamsims-updater"), paths.Root);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.Combine(home, "AppData", "Roaming", "lamsims-updater"), paths.Root);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.Equal(
                Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg,
                             "lamsims-updater"),
                paths.Root);
        }
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
