using LamSims.Core.Settings;

namespace LamSims.Core.Tests;

public class AppPathsTests
{
    [Fact]
    public void Defaults_under_the_application_data_directory()
    {
        var paths = new AppPaths();

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "lamsims-updater");

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
}
