using LamSims.Core.Catalogs;
using LamSims.Core.Queueing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using LamSims.App.Services;
using LamSims.App.ViewModels;
using LamSims.Core.Downloading;
using LamSims.Core.Settings;

namespace LamSims.App.Tests;

public class MainViewModelLifecycleTests
{
    private static QueueItemSnapshot Snap(string code, QueueItemState state) =>
        new(code, code, state, 0, 0, 0, null, null, null, Array.Empty<string>());

    /// <summary>
    /// Since this instance was constructed, which xunit does per test method. The budget below is
    /// spent across the whole test rather than reset per wait: two sequential 10s waits inside one
    /// 15s [Fact] would let xunit's timeout fire first, and the named diagnostic this exists to
    /// produce would never be seen.
    /// </summary>
    private readonly Stopwatch _test = Stopwatch.StartNew();

    /// <summary>
    /// Just inside every [Fact(Timeout = 15000)] here, so a hang always reports WHICH condition
    /// never held instead of xunit's bare timeout, while leaving a correct-but-slow run the same
    /// margin it always had.
    /// </summary>
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(13);

    // No sleeping: yielding is what lets the work being waited for run.
    // CallerArgumentExpression supplies the condition's own source text, so no call site has to
    // describe itself.
    private async Task WaitUntil(Func<bool> condition,
        [CallerArgumentExpression(nameof(condition))] string? text = null)
    {
        while (!condition())
        {
            if (_test.Elapsed > TestBudget)
            {
                throw new TimeoutException(
                    $"`{text}` was still false {TestBudget.TotalSeconds:0}s into the test.");
            }

            await Task.Yield();
        }
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
    public async Task Shutdown_flushes_a_pending_settings_change_before_it_touches_the_queue()
    {
        // With the save recorded in its own list and the queue calls in another, moving the
        // flush after DisposeAsync would leave both lists unchanged, so the save is recorded
        // into the SAME list the queue records into and the interleaving is pinned.
        var order = new List<string>();
        AppSettings? flushed = null;

        var vm = TestHost.ViewModel(out var host,
            save: (settings, _) =>
            {
                order.Add("Save");
                flushed = settings;
                return Task.CompletedTask;
            },
            queue: new RecordingQueue(order));
        using var _h = host;
        vm.Connections = 5;

        await vm.ShutdownAsync();

        Assert.Equal(["Save", "CancelAll", "Dispose"], order);
        Assert.NotNull(flushed);
        Assert.Equal(5, flushed.Connections);
    }

    [Fact(Timeout = 15000)]
    public async Task Shutdown_disposes_the_queue_even_when_CancelAll_throws()
    {
        // PackQueue.CancelAll ends in cts.Cancel(), which runs registered cancellation callbacks
        // synchronously on this thread and rethrows them wrapped in an AggregateException. With
        // CancelAll and DisposeAsync guarded by one try, that throw skips the disposal: _completed
        // is never set, so the loop keeps taking Queued items and installing packs after the user
        // closed the window, with the window held open by MainWindow's re-entrancy guard while it
        // happens. Both halves are asserted: the banner alone would pass for a shutdown that
        // reported the failure and then abandoned the queue.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        host.Queue.CancelAllThrows = new AggregateException(new OperationCanceledException());

        await vm.ShutdownAsync();

        Assert.Equal(["CancelAll", "Dispose"], host.Queue.Calls);
        Assert.Contains(vm.Banners, b => b.Id == "queue" && b.Kind == BannerKind.Error);
    }

    [Fact(Timeout = 15000)]
    public async Task Starting_twice_runs_the_queue_and_builds_the_bridge_only_once()
    {
        // MainWindow.OnOpened is an async void override and is the only caller, so nothing in the
        // framework guarantees it runs once. A second StartAsync would build a second QueueBridge
        // over a single-reader channel, which the bridge's own remarks call a defect rather than a
        // degradation. "Run" appearing once is the observable for both: they are started together.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;

        await vm.StartAsync(CancellationToken.None);
        await vm.StartAsync(CancellationToken.None);

        Assert.Equal(["Run"], host.Queue.Calls);

        await vm.ShutdownAsync();
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
