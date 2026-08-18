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
}
