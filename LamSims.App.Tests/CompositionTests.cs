using System.Text.Json;
using LamSims.App;

namespace LamSims.App.Tests;

public class CompositionTests
{
    [Fact(Timeout = 15000)]
    public async Task The_graph_builds_inside_the_root_it_is_given()
    {
        // The override must reach the DOWNLOAD directory too. DownloadPaths defaults to the
        // real user profile, so a graph that only redirects AppPaths writes .part files,
        // archives, quarantines and lock files into the developer's actual download folder.
        var root = Directory.CreateTempSubdirectory("lamsims-composition").FullName;

        try
        {
            var services = Composition.Build(commandLineCatalog: null, overrideRoot: root);

            Assert.StartsWith(root, services.Paths.Root, StringComparison.Ordinal);
            Assert.Equal(services.Paths.InstallStateDirectory, services.InstallState.Root);
            Assert.True(Directory.Exists(Path.Combine(root, "downloads")));

            await services.Queue.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 15000)]
    public async Task A_download_directory_that_cannot_be_created_banners_rather_than_killing_the_launch()
    {
        // The shipped case: the user puts downloads on a removable drive and unplugs it.
        // Composition runs from OnFrameworkInitializationCompleted BEFORE MainWindow exists and
        // Program.Main installs no handler, so throwing here means no window ever opens, and the
        // setting that caused it can only be changed in that window. PackQueue already guards the
        // same call so the queue can banner it; composition ran it first and unguarded.
        var root = Directory.CreateTempSubdirectory("lamsims-composition").FullName;

        try
        {
            // A file where the directory must go. Portable, and needs no permission change.
            var unusable = Path.Combine(root, "unplugged");
            File.WriteAllText(unusable, "");

            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(
                Path.Combine(root, "config", "settings.json"),
                $"{{\"downloadDirectory\":{JsonSerializer.Serialize(unusable)}}}");

            var services = Composition.Build(commandLineCatalog: null, overrideRoot: root);

            // The pair. "It did not throw" alone would also hold for a build that silently used
            // some other directory and never told the user their setting was ignored.
            Assert.NotNull(services.SettingsError);
            Assert.Contains(unusable, services.SettingsError);
            Assert.True(Directory.Exists(Path.Combine(root, "downloads")));

            await services.Queue.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 15000)]
    public async Task A_configuration_root_that_cannot_be_created_banners_rather_than_killing_the_launch()
    {
        var root = Directory.CreateTempSubdirectory("lamsims-composition").FullName;

        try
        {
            // A file where AppPaths.Root must go.
            File.WriteAllText(Path.Combine(root, "config"), "");

            var services = Composition.Build(commandLineCatalog: null, overrideRoot: root);

            Assert.NotNull(services.SettingsError);
            Assert.Contains("config", services.SettingsError);

            await services.Queue.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The unlocker leg of the graph. Nothing else asserts it: every other test injects its own
    /// service, and a graph built with no backend at all passes the whole suite while shipping an
    /// unlocker that can never find a client. Both ids are pinned, and in order, because each
    /// backend answers IsSupported for one platform and a graph missing either one leaves that
    /// platform with no unlocker.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task The_graph_registers_both_unlocker_backends_in_order()
    {
        var root = Directory.CreateTempSubdirectory("lamsims-composition-unlocker").FullName;

        try
        {
            var services = Composition.Build(commandLineCatalog: null, overrideRoot: root);

            Assert.Equal(["windows-native"], services.Unlocker.BackendIds);
            Assert.IsType<LamSims.Core.Unlocking.WindowsUnlockerHost>(services.UnlockerHost);
            Assert.IsType<LamSims.Core.Unlocking.StaticUnlockerAssetSource>(services.UnlockerAssets);

            await services.Queue.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
