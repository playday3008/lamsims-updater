using System.Net;
using System.Text;
using LamSims.Core.Catalogs;
using LamSims.Core.Settings;

namespace LamSims.Core.Tests;

public class CatalogLoaderTests
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string CatalogJson(string code) => $$"""
        {
          "schemaVersion": 1,
          "packs": [
            {
              "code": "{{code}}",
              "name": "Pack {{code}}",
              "type": "expansion",
              "size": 1024,
              "sha256": "{{Digest}}",
              "urls": ["https://host-a.example.invalid/{{code}}.zip"]
            }
          ]
        }
        """;

    private static CatalogLoader Loader(TempDir temp, HttpClient? client = null, string? executableDirectory = null) =>
        new(client ?? new HttpClient(),
            new AppPaths(Path.Combine(temp.Path, "appdata")),
            executableDirectory ?? Path.Combine(temp.Path, "bin"));

    private static string WriteCatalog(TempDir temp, string relativePath, string code)
    {
        var full = Path.Combine(temp.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, CatalogJson(code));
        return full;
    }

    [Fact]
    public async Task Prefers_the_command_line_source()
    {
        using var temp = new TempDir();
        var commandLine = WriteCatalog(temp, "cli/catalog.json", "EP01");
        var settings = WriteCatalog(temp, "settings/catalog.json", "EP02");

        var resolution = await Loader(temp).ResolveAsync(commandLine, settings, CancellationToken.None);

        Assert.Equal(CatalogStatus.Loaded, resolution.Status);
        Assert.Equal(CatalogSourceKind.CommandLine, resolution.Source!.Kind);
        Assert.Equal("EP01", Assert.Single(resolution.Catalog!.Catalog.Packs).Code);
    }

    [Fact]
    public async Task Falls_back_to_the_settings_source()
    {
        using var temp = new TempDir();
        var settings = WriteCatalog(temp, "settings/catalog.json", "EP02");

        var resolution = await Loader(temp).ResolveAsync(null, settings, CancellationToken.None);

        Assert.Equal(CatalogSourceKind.Settings, resolution.Source!.Kind);
        Assert.Equal("EP02", Assert.Single(resolution.Catalog!.Catalog.Packs).Code);
    }

    [Fact]
    public async Task Falls_back_to_the_catalog_beside_the_executable()
    {
        using var temp = new TempDir();
        WriteCatalog(temp, "bin/catalog.json", "EP03");

        var resolution = await Loader(temp).ResolveAsync(null, null, CancellationToken.None);

        Assert.Equal(CatalogSourceKind.BesideExecutable, resolution.Source!.Kind);
        Assert.Equal("EP03", Assert.Single(resolution.Catalog!.Catalog.Packs).Code);
    }

    [Fact]
    public async Task Falls_back_to_the_cached_copy()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.CatalogCacheFile, CatalogJson("EP04"));

        var resolution = await Loader(temp).ResolveAsync(null, null, CancellationToken.None);

        Assert.Equal(CatalogSourceKind.Cache, resolution.Source!.Kind);
        Assert.Equal("EP04", Assert.Single(resolution.Catalog!.Catalog.Packs).Code);
    }

    [Fact]
    public async Task Reports_an_empty_state_when_nothing_resolves()
    {
        using var temp = new TempDir();

        var resolution = await Loader(temp).ResolveAsync(null, null, CancellationToken.None);

        Assert.Equal(CatalogStatus.Empty, resolution.Status);
        Assert.Null(resolution.Error);
        Assert.Null(resolution.Catalog);
        Assert.False(resolution.CachedCopyAvailable);
    }

    [Fact]
    public async Task Surfaces_a_command_line_source_that_does_not_exist()
    {
        using var temp = new TempDir();
        WriteCatalog(temp, "bin/catalog.json", "EP03");
        var missing = Path.Combine(temp.Path, "typo.json");

        var resolution = await Loader(temp).ResolveAsync(missing, null, CancellationToken.None);

        // Falling through to the catalog beside the executable would hide the user's typo.
        Assert.Equal(CatalogStatus.Failed, resolution.Status);
        Assert.Equal(CatalogSourceKind.CommandLine, resolution.Source!.Kind);
        Assert.Contains("typo.json", resolution.Error);
    }

    [Fact]
    public async Task Surfaces_a_settings_source_that_fails_to_parse_and_offers_the_cache()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.CatalogCacheFile, CatalogJson("EP04"));

        var broken = Path.Combine(temp.Path, "broken.json");
        await File.WriteAllTextAsync(broken, "{ not json");

        var resolution = await Loader(temp).ResolveAsync(null, broken, CancellationToken.None);

        Assert.Equal(CatalogStatus.Failed, resolution.Status);
        Assert.Contains("broken.json", resolution.Error);
        Assert.True(resolution.CachedCopyAvailable);
        Assert.Null(resolution.Catalog);
    }

    [Fact]
    public async Task Loads_the_cache_when_it_is_chosen_explicitly_after_a_failure()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.CatalogCacheFile, CatalogJson("EP04"));

        var loader = Loader(temp);
        var chosen = new CatalogSource(CatalogSourceKind.Cache, paths.CatalogCacheFile);

        var result = await loader.LoadAsync(chosen, CancellationToken.None);

        Assert.Equal("EP04", Assert.Single(result.Catalog.Packs).Code);
    }

    [Fact]
    public async Task Passes_over_a_damaged_catalog_beside_the_executable()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.CatalogCacheFile, CatalogJson("EP04"));

        Directory.CreateDirectory(Path.Combine(temp.Path, "bin"));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "bin", "catalog.json"), "{ not json");

        var resolution = await Loader(temp).ResolveAsync(null, null, CancellationToken.None);

        // Nobody chose this file, so it is a fallback that did not work out, not an error.
        Assert.Equal(CatalogStatus.Loaded, resolution.Status);
        Assert.Equal(CatalogSourceKind.Cache, resolution.Source!.Kind);
    }

    [Fact]
    public async Task Fetches_a_remote_catalog_and_caches_it()
    {
        using var temp = new TempDir();
        await using var server = await TestFileServer.StartAsync(Encoding.UTF8.GetBytes(CatalogJson("EP05")));
        using var client = new HttpClient();

        var paths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        var loader = new CatalogLoader(client, paths, Path.Combine(temp.Path, "bin"));
        var url = server.FileUrl.ToString();

        var resolution = await loader.ResolveAsync(url, null, CancellationToken.None);

        Assert.Equal(CatalogStatus.Loaded, resolution.Status);
        Assert.Equal("EP05", Assert.Single(resolution.Catalog!.Catalog.Packs).Code);
        Assert.True(File.Exists(paths.CatalogCacheFile));
        Assert.Equal(CatalogJson("EP05"), await File.ReadAllTextAsync(paths.CatalogCacheFile));
    }

    [Fact]
    public async Task Surfaces_a_remote_catalog_that_answers_with_an_error()
    {
        using var temp = new TempDir();
        await using var server = await TestFileServer.StartAsync(
            Encoding.UTF8.GetBytes(CatalogJson("EP05")),
            new TestFileServerOptions { FailNextRequests = 1 });
        using var client = new HttpClient();

        var paths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        var loader = new CatalogLoader(client, paths, Path.Combine(temp.Path, "bin"));
        var url = server.FileUrl.ToString();

        // The loader makes one attempt per source; unlike the download engine it does not
        // retry, because a catalog is small and the user is waiting on the pack list.
        var resolution = await loader.ResolveAsync(url, null, CancellationToken.None);

        Assert.Equal(CatalogStatus.Failed, resolution.Status);
        Assert.Contains(url, resolution.Error);
        Assert.False(File.Exists(paths.CatalogCacheFile));
    }

    [Fact]
    public async Task Never_caches_a_remote_body_that_does_not_parse()
    {
        using var temp = new TempDir();
        await using var server = await TestFileServer.StartAsync(Encoding.UTF8.GetBytes("{ not json"));
        using var client = new HttpClient();

        var paths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        var loader = new CatalogLoader(client, paths, Path.Combine(temp.Path, "bin"));

        var resolution = await loader.ResolveAsync(
            server.FileUrl.ToString(), null, CancellationToken.None);

        Assert.Equal(CatalogStatus.Failed, resolution.Status);
        Assert.False(File.Exists(paths.CatalogCacheFile));
    }

    [Fact]
    public async Task Reports_a_stalling_remote_source_as_failed_instead_of_throwing()
    {
        using var temp = new TempDir();
        await using var server = await TestFileServer.StartAsync(
            Encoding.UTF8.GetBytes(CatalogJson("EP05")),
            new TestFileServerOptions { StallBeforeHeaders = TimeSpan.FromSeconds(30) });
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(150) };

        var paths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        var loader = new CatalogLoader(client, paths, Path.Combine(temp.Path, "bin"));
        var url = server.FileUrl.ToString();

        // HttpClient.Timeout throws TaskCanceledException, not HttpRequestException; this
        // must still be reported as a source failure rather than escaping ResolveAsync.
        var resolution = await loader.ResolveAsync(url, null, CancellationToken.None);

        Assert.Equal(CatalogStatus.Failed, resolution.Status);
        Assert.Contains(url, resolution.Error);
        Assert.False(File.Exists(paths.CatalogCacheFile));
    }

    [Fact]
    public async Task Surfaces_a_command_line_path_the_filesystem_rejects()
    {
        using var temp = new TempDir();
        var malformed = "typo\0.json";

        var resolution = await Loader(temp).ResolveAsync(malformed, null, CancellationToken.None);

        Assert.Equal(CatalogStatus.Failed, resolution.Status);
        Assert.Equal(CatalogSourceKind.CommandLine, resolution.Source!.Kind);
    }

    [Fact]
    public async Task Propagates_a_genuine_cancellation_instead_of_reporting_it()
    {
        using var temp = new TempDir();
        var commandLine = WriteCatalog(temp, "cli/catalog.json", "EP01");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Loader(temp).ResolveAsync(commandLine, null, cts.Token));
    }

    [Fact]
    public void Recognises_which_sources_are_remote()
    {
        Assert.True(new CatalogSource(CatalogSourceKind.Settings, "https://a.example.invalid/c.json").IsRemote);
        Assert.True(new CatalogSource(CatalogSourceKind.Settings, "http://a.example.invalid/c.json").IsRemote);
        Assert.False(new CatalogSource(CatalogSourceKind.Settings, "/srv/catalog.json").IsRemote);
        Assert.False(new CatalogSource(CatalogSourceKind.Settings, @"C:\catalog.json").IsRemote);
    }

    [Fact]
    public void Lists_only_the_candidates_that_are_present()
    {
        using var temp = new TempDir();

        var candidates = Loader(temp).Candidates(null, "   ");

        Assert.Empty(candidates);
    }
}
