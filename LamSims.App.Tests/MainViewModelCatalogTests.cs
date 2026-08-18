using LamSims.App.ViewModels;

namespace LamSims.App.Tests;

public class MainViewModelCatalogTests
{
    private static QueueItemSnapshot Snap(string code, QueueItemState state) =>
        new(code, code, state, 0, 0, 0, null, null, null, Array.Empty<string>());

    [Fact(Timeout = 15000)]
    public async Task An_empty_resolution_is_an_empty_state_not_an_error()
    {
        var vm = TestHost.ViewModel(out var host, resolve: _ =>
            Task.FromResult(new CatalogResolution(CatalogStatus.Empty, null, null, null, null)));
        using var _h = host;

        await vm.LoadCatalogAsync(CancellationToken.None);

        Assert.Empty(vm.Rows);
        Assert.Equal("No catalog selected.", vm.EmptyStateMessage);
        Assert.Empty(vm.Banners);
    }

    [Fact(Timeout = 15000)]
    public async Task A_failed_resolution_banners_the_error_and_offers_the_cached_copy()
    {
        var cached = new CatalogSource(CatalogSourceKind.Cache, "/tmp/catalog.cache.json");
        var vm = TestHost.ViewModel(out var host, resolve: _ =>
            Task.FromResult(new CatalogResolution(CatalogStatus.Failed, null, null, "the mirror returned 500", cached)));
        using var _h = host;

        await vm.LoadCatalogAsync(CancellationToken.None);

        Assert.Contains(vm.Banners, b => b.Kind == BannerKind.Error && b.Text.Contains("the mirror returned 500"));
        Assert.True(vm.UseCachedCatalogCommand.CanExecute(null));
    }

    [Fact(Timeout = 15000)]
    public async Task The_cached_copy_is_loaded_through_LoadAsync_not_by_resolving_again()
    {
        // Re-resolving walks the same candidate chain and fails against the same source.
        // LoadAsync is the only entry point core gives for loading a named source, and core
        // documents it as existing for exactly this flow.
        var cached = new CatalogSource(CatalogSourceKind.Cache, "/tmp/catalog.cache.json");
        var loaded = new List<CatalogSource>();
        var resolves = 0;

        var vm = TestHost.ViewModel(out var host,
            resolve: _ =>
            {
                resolves++;
                return Task.FromResult(new CatalogResolution(CatalogStatus.Failed, null, null, "down", cached));
            },
            load: (source, _) =>
            {
                loaded.Add(source);
                return Task.FromResult(new CatalogResolution(
                    CatalogStatus.Loaded,
                    new CatalogLoadResult(new Catalog(1, null, [Packs.Entry()]), []),
                    source, null, null));
            });
        using var _h = host;

        await vm.LoadCatalogAsync(CancellationToken.None);
        await vm.UseCachedCatalogAsync(CancellationToken.None);

        Assert.Equal([cached], loaded);
        Assert.Equal(1, resolves);
        Assert.Single(vm.Rows);
        Assert.DoesNotContain(vm.Banners, b => b.Id == "catalog");
    }

    [Fact(Timeout = 15000)]
    public async Task Rejected_entries_are_one_banner_and_do_not_block_the_rest()
    {
        var vm = TestHost.ViewModel(out var host, resolve: _ =>
            Task.FromResult(new CatalogResolution(
                CatalogStatus.Loaded,
                new CatalogLoadResult(
                    new Catalog(1, null, [Packs.Entry()]),
                    [new RejectedEntry("SP99", "its size was negative")]),
                new CatalogSource(CatalogSourceKind.Settings, "/tmp/catalog.json"), null, null)));
        using var _h = host;

        await vm.LoadCatalogAsync(CancellationToken.None);

        Assert.Single(vm.Rows);
        var banner = Assert.Single(vm.Banners);
        Assert.Equal(BannerKind.Warning, banner.Kind);
        Assert.Contains("SP99", banner.Text);
    }

    [Fact(Timeout = 15000)]
    public async Task Cancellation_during_resolution_is_not_reported_as_a_catalog_error()
    {
        // ResolveAsync propagates the caller's cancellation as OperationCanceledException
        // rather than folding it into Failed. A call site that does not catch it turns closing
        // the window during startup into a spurious error.
        var vm = TestHost.ViewModel(out var host,
            resolve: _ => throw new OperationCanceledException());
        using var _h = host;

        await vm.LoadCatalogAsync(CancellationToken.None);

        Assert.Empty(vm.Banners);
    }

    [Fact]
    public void A_settings_load_error_is_bannered_at_construction()
    {
        var vm = TestHost.ViewModel(out var host, settingsError: "settings.json was not valid JSON");
        using var _h = host;

        Assert.Contains(vm.Banners, b => b.Id == "settings-load" && b.Text.Contains("not valid JSON"));
    }

    [Fact]
    public void Changing_the_catalog_is_refused_while_work_is_in_the_queue()
    {
        // Rebuilding rows under a live queue orphans an in-flight pack's row while it keeps its
        // lock and its bandwidth. Requiring an empty queue removes the whole class.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;

        vm.ApplyUpdate(new QueueUpdate(QueueState.Running, [Snap("EP01", QueueItemState.Downloading)]));
        Assert.False(vm.ChangeCatalogCommand.CanExecute(null));

        vm.ApplyUpdate(new QueueUpdate(QueueState.Idle, [Snap("EP01", QueueItemState.Completed)]));
        Assert.True(vm.ChangeCatalogCommand.CanExecute(null));
    }
}
