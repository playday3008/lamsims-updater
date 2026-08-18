using LamSims.App.Services;
using LamSims.App.ViewModels;
using LamSims.Core.Downloading;

namespace LamSims.App.Tests;

public class MainViewModelLifecycleTests
{
    private static QueueItemSnapshot Snap(string code, QueueItemState state) =>
        new(code, code, state, 0, 0, 0, null, null, null, Array.Empty<string>());

    // No sleeping: the [Fact] timeout is what turns a condition that never holds into a red.
    private static async Task WaitUntil(Func<bool> condition)
    {
        while (!condition()) await Task.Yield();
    }

    // ---- feature detector 3 ----
    [Fact(Timeout = 15000)]
    public async Task Shutdown_cancels_before_disposing_and_never_completes_the_queue()
    {
        // Complete() is the !_completed half of RequeueBlocked's guard, so calling it makes a
        // blocked pack eligible to start; it also clears a standing pause and releases the
        // loop's signal. The loop can then take a pack lock and reach ZipInstaller, which
        // writes its journal marker before the first entry, leaving a Partial install nobody
        // asked for.
        //
        // Against a real queue this rule has no deterministic observable: both orderings end in
        // cancellation microseconds apart, and which one wins is a race. The recorded call
        // sequence is the observable, so this asserts the sequence exactly.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;

        await vm.ShutdownAsync();

        Assert.Equal(["CancelAll", "Dispose"], host.Queue.Calls);
        Assert.DoesNotContain("Complete", host.Queue.Calls);
    }

    [Fact(Timeout = 15000)]
    public async Task Shutdown_flushes_a_pending_settings_change_first()
    {
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        vm.Connections = 5;

        await vm.ShutdownAsync();

        Assert.Single(host.Saved);
        Assert.Equal(5, host.Saved[0].Connections);
    }

    [Fact(Timeout = 15000)]
    public async Task Shutdown_is_idempotent()
    {
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;

        await vm.ShutdownAsync();
        await vm.ShutdownAsync();

        Assert.Equal(["CancelAll", "Dispose"], host.Queue.Calls);
        Assert.True(vm.IsShuttingDown);
    }

    [Fact]
    public void A_queue_that_dies_disables_every_control_and_says_so()
    {
        // RunAsync can fault: EnsureCreated throws on a read-only root or a file where the
        // directory should be. Without an observer the queue is dead, Enqueue returns silently,
        // and the UI keeps accepting Add forever with no feedback.
        //
        // Every command below is put into a state where it WOULD be enabled, so each assertion
        // discriminates on IsQueueAlive and nothing else.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        vm.GameDirectory = host.GameDirectory;
        vm.ApplyUpdate(new QueueUpdate(QueueState.Running, [Snap("EP01", QueueItemState.Downloading)]));

        Assert.True(vm.AddSelectedCommand.CanExecute(null));
        Assert.True(vm.PauseCommand.CanExecute(null));
        Assert.True(vm.CancelAllCommand.CanExecute(null));

        vm.NoteQueueClosed(new IOException("the download folder could not be created"));

        Assert.False(vm.IsQueueAlive);
        Assert.False(vm.AddSelectedCommand.CanExecute(null));
        Assert.False(vm.PauseCommand.CanExecute(null));
        Assert.False(vm.CancelAllCommand.CanExecute(null));
        Assert.Contains(vm.Banners, b => b.Id == "queue" && b.Kind == BannerKind.Error);
    }

    [Fact]
    public void A_queue_that_closes_cleanly_is_not_an_error()
    {
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;

        vm.NoteQueueClosed(null);

        Assert.False(vm.IsQueueAlive);
        Assert.DoesNotContain(vm.Banners, b => b.Id == "queue");
    }

    [Fact(Timeout = 15000)]
    public async Task A_real_queue_that_cannot_create_its_download_folder_raises_the_banner()
    {
        // The two tests above call NoteQueueClosed directly, which proves the reaction and not
        // the wiring. This drives the only fault a real PackQueue actually produces: EnsureCreated
        // is the first statement inside RunAsync's try (PackQueue.cs:519) precisely so this throws
        // where the finally can still close the channel, and it faults the TASK, never the
        // channel, so a shell that watches only the bridge sees nothing.
        var root = Directory.CreateTempSubdirectory("lamsims-deadqueue").FullName;

        try
        {
            // A plain file where the downloads directory should be: Directory.CreateDirectory
            // throws IOException on it.
            var blocked = Path.Combine(root, "downloads");
            File.WriteAllText(blocked, "not a directory");

            var controller = new PackQueueController(
                new PackQueue(new ScriptedRunner(), new DownloadPaths(blocked)));

            var vm = TestHost.ViewModel(out var host, queue: controller);
            using var _h = host;

            await vm.StartAsync(CancellationToken.None);

            try
            {
                await WaitUntil(() => !vm.IsQueueAlive);

                Assert.Contains(vm.Banners, b => b.Id == "queue" && b.Kind == BannerKind.Error);
                Assert.False(vm.AddSelectedCommand.CanExecute(null));
            }
            finally
            {
                await vm.ShutdownAsync();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 15000)]
    public async Task Startup_runs_the_queue_loads_the_catalog_and_scans()
    {
        var vm = TestHost.ViewModel(out var host, resolve: _ =>
            Task.FromResult(new CatalogResolution(
                CatalogStatus.Loaded,
                new CatalogLoadResult(new Catalog(1, null, [Packs.Entry()]), []),
                new CatalogSource(CatalogSourceKind.Settings, "/tmp/catalog.json"), null, null)),
            seed: s => s.GameDirectory = null);
        using var _h = host;
        vm.GameDirectory = host.GameDirectory;

        await vm.StartAsync(CancellationToken.None);

        Assert.Contains("Run", host.Queue.Calls);
        Assert.Single(vm.Rows);
        Assert.True(vm.ScanCount > 0);

        host.Queue.Publish(new QueueUpdate(QueueState.Running, [Snap("EP01", QueueItemState.Downloading)]));
        await WaitUntil(() => vm.PendingCount == 1);

        await vm.ShutdownAsync();
    }
}
