using System.Collections.Concurrent;
using LamSims.App.ViewModels;
using LamSims.Core.Unlocking;

namespace LamSims.App.Tests;

public class UnlockerViewModelTests
{
    private static UnlockerTarget Target(string path = "/clients/ea", string display = "EA app",
        ClientKind kind = ClientKind.EaApp) =>
        new("test-backend", kind, path, display);

    private static UnlockerViewModel Build(
        RecordingUnlockerBackend backend,
        IUnlockerHost? host = null,
        Func<Task>? shutdown = null,
        Action? exit = null,
        Action<Banner>? banner = null) =>
        new(new UnlockerService([backend]), new StubUnlockerAssets(), host ?? new FakeUnlockerHost(),
            new ImmediateDispatcher(), shutdown ?? (() => Task.CompletedTask), exit ?? (() => { }),
            banner ?? (_ => { }));

    [Fact]
    public async Task Unsupported_service_reports_no_targets_and_no_support()
    {
        var vm = new UnlockerViewModel(new UnlockerService([]), new StubUnlockerAssets(),
            new FakeUnlockerHost(), new ImmediateDispatcher(), () => Task.CompletedTask, () => { },
            _ => { });

        Assert.False(vm.IsSupported);

        await vm.RefreshAsync(CancellationToken.None);

        Assert.Empty(vm.Targets);
    }

    [Fact]
    public async Task RefreshAsync_builds_one_row_per_detected_target()
    {
        var target = Target();
        var backend = new RecordingUnlockerBackend(target) { State = UnlockerState.Installed };
        var vm = Build(backend);

        await vm.RefreshAsync(CancellationToken.None);

        var row = Assert.Single(vm.Targets);
        Assert.Equal("EA app", row.DisplayName);
        Assert.Equal("/clients/ea", row.ClientPath);
        Assert.Equal("Installed", row.StatusText);
    }

    // ---- pair: InstallCommand must reach the SERVICE with the ROW'S OWN target, never the
    // collection's first entry. Asserting only the verb ("Install ran") would also pass a row
    // wired to its neighbour's data context. ----
    [Fact]
    public async Task InstallCommand_reaches_InstallAsync_with_this_rows_own_target()
    {
        var first = Target("/clients/ea", "EA app", ClientKind.EaApp);
        var second = Target("/clients/origin", "Origin", ClientKind.Origin);
        var backend = new RecordingUnlockerBackend(first, second);
        var vm = Build(backend);
        await vm.RefreshAsync(CancellationToken.None);

        var row = vm.Targets.Single(t => t.ClientPath == "/clients/origin");
        await row.InstallCommand.ExecuteAsync(null);

        Assert.Contains("Install:/clients/origin", backend.Calls);
        Assert.DoesNotContain("Install:/clients/ea", backend.Calls);
    }

    [Fact]
    public async Task RemoveCommand_reaches_RemoveAsync_with_this_rows_own_target()
    {
        var first = Target("/clients/ea", "EA app", ClientKind.EaApp);
        var second = Target("/clients/origin", "Origin", ClientKind.Origin);
        var backend = new RecordingUnlockerBackend(first, second);
        var vm = Build(backend);
        await vm.RefreshAsync(CancellationToken.None);

        var row = vm.Targets.Single(t => t.ClientPath == "/clients/origin");
        await row.RemoveCommand.ExecuteAsync(null);

        Assert.Contains("Remove:/clients/origin", backend.Calls);
        Assert.DoesNotContain("Remove:/clients/ea", backend.Calls);
    }

    [Fact]
    public async Task Progress_reports_update_step_completed_and_total()
    {
        var target = Target();
        var backend = new RecordingUnlockerBackend(target);
        backend.ToReport.Add(new UnlockerProgress("Stopping the client", 1, 3));
        backend.ToReport.Add(new UnlockerProgress("Done", 3, 3));
        var vm = Build(backend);
        await vm.RefreshAsync(CancellationToken.None);

        await vm.Targets[0].InstallCommand.ExecuteAsync(null);

        Assert.Equal("Done", vm.CurrentStep);
        Assert.Equal(3, vm.Completed);
        Assert.Equal(3, vm.Total);
    }

    [Fact]
    public async Task IsBusy_is_true_during_the_operation_and_disables_both_row_commands()
    {
        var target = Target();
        var backend = new RecordingUnlockerBackend(target);
        var hold = new TaskCompletionSource();
        backend.Hold = hold;
        var vm = Build(backend);
        await vm.RefreshAsync(CancellationToken.None);
        var row = vm.Targets[0];

        var running = row.InstallCommand.ExecuteAsync(null);

        // SetBusy(true) runs synchronously before the operation's Task.Run yields, so this holds
        // without waiting: the caller does not get its Task back until that point is reached.
        Assert.True(vm.IsBusy);
        Assert.True(row.IsBusy);
        Assert.False(row.InstallCommand.CanExecute(null));
        Assert.False(row.RemoveCommand.CanExecute(null));

        hold.SetResult();
        await running;

        Assert.False(vm.IsBusy);
        Assert.False(row.IsBusy);
        Assert.True(row.InstallCommand.CanExecute(null));
        Assert.True(row.RemoveCommand.CanExecute(null));
    }

    // Gap 2 from review round 1: SetBusy's loop was only ever exercised with one row in the
    // collection, so a regression narrowing it to just the acted-on row would have passed. This
    // needs a SECOND, untouched row to prove the busy state (and the disabled commands) is
    // shared across every row, not scoped to the one the operation is running against.
    [Fact]
    public async Task Acting_on_one_row_disables_every_other_row_too()
    {
        var acted = Target("/clients/ea", "EA app", ClientKind.EaApp);
        var bystander = Target("/clients/origin", "Origin", ClientKind.Origin);
        var backend = new RecordingUnlockerBackend(acted, bystander);
        var hold = new TaskCompletionSource();
        backend.Hold = hold;
        var vm = Build(backend);
        await vm.RefreshAsync(CancellationToken.None);
        var rowActedOn = vm.Targets.Single(t => t.ClientPath == "/clients/ea");
        var rowBystander = vm.Targets.Single(t => t.ClientPath == "/clients/origin");

        var running = rowActedOn.InstallCommand.ExecuteAsync(null);

        Assert.True(rowBystander.IsBusy);
        Assert.False(rowBystander.InstallCommand.CanExecute(null));
        Assert.False(rowBystander.RemoveCommand.CanExecute(null));

        hold.SetResult();
        await running;

        Assert.False(rowBystander.IsBusy);
        Assert.True(rowBystander.InstallCommand.CanExecute(null));
        Assert.True(rowBystander.RemoveCommand.CanExecute(null));
    }

    // ---- pair: a RequiresElevation result must both raise the banner AND arm the relaunch
    // command. A banner with no live command would strand the user on a dead button. ----
    [Fact]
    public async Task NeedsElevation_result_raises_a_warning_banner_and_arms_relaunch()
    {
        var target = Target();
        var backend = new RecordingUnlockerBackend(target) { NextResult = UnlockerResult.NeedsElevation() };
        var banners = new List<Banner>();
        var vm = Build(backend, banner: banners.Add);
        await vm.RefreshAsync(CancellationToken.None);

        Assert.False(vm.RelaunchElevatedCommand.CanExecute(null));

        await vm.Targets[0].InstallCommand.ExecuteAsync(null);

        Assert.True(vm.RequiresElevation);
        Assert.True(vm.RelaunchElevatedCommand.CanExecute(null));
        Assert.Contains(banners, b => b.Id == "unlocker-elevation" && b.Kind == BannerKind.Warning);
    }

    /// <summary>Mirrors MainViewModel.ShutdownAsync's one-way guard and its queue disposal, at the
    /// grain UnlockerViewModel actually depends on: an idempotent shutdown that records into a
    /// SHARED list so the relaunch-then-shutdown order is one observable, exactly as
    /// MainViewModelLifecycleTests pins the queue's own ordering.</summary>
    private sealed class FakeShutdown(List<string> order)
    {
        public bool IsShuttingDown { get; private set; }
        public bool QueueDisposed { get; private set; }

        public Task RunAsync()
        {
            if (IsShuttingDown) return Task.CompletedTask;

            IsShuttingDown = true;
            order.Add("CancelAll");
            order.Add("Dispose");
            QueueDisposed = true;

            return Task.CompletedTask;
        }
    }

    // ---- pair: relaunch accepted -> host first, shutdown second, in that exact order. ----
    [Fact]
    public async Task RelaunchElevatedCommand_relaunches_the_host_before_shutting_down()
    {
        var order = new List<string>();
        var host = new FakeUnlockerHost(order) { RelaunchAccepted = true };
        var shutdown = new FakeShutdown(order);
        var backend = new RecordingUnlockerBackend(Target()) { NextResult = UnlockerResult.NeedsElevation() };
        var vm = Build(backend, host: host, shutdown: shutdown.RunAsync,
                       exit: () => order.Add("Exit"));
        await vm.RefreshAsync(CancellationToken.None);
        await vm.Targets[0].InstallCommand.ExecuteAsync(null);

        await vm.RelaunchElevatedCommand.ExecuteAsync(null);

        // Exit last, and only after shutdown: shutdown does not close the window, so without it the
        // accepted prompt leaves a greyed shell beside the elevated instance for ever.
        Assert.Equal(["Relaunch", "CancelAll", "Dispose", "Exit"], order);
        Assert.True(shutdown.IsShuttingDown);
    }

    // Relaunch declined: the host was asked, but nothing about the shell was torn down. A
    // banner-only assertion would also pass a shell that shut itself down first and raised the
    // banner afterwards.
    [Fact]
    public async Task Declined_relaunch_leaves_the_shell_running_with_a_live_queue()
    {
        var order = new List<string>();
        var host = new FakeUnlockerHost(order) { RelaunchAccepted = false };
        var shutdown = new FakeShutdown(order);
        var backend = new RecordingUnlockerBackend(Target()) { NextResult = UnlockerResult.NeedsElevation() };
        var banners = new List<Banner>();
        var vm = Build(backend, host: host, shutdown: shutdown.RunAsync, banner: banners.Add);
        await vm.RefreshAsync(CancellationToken.None);
        await vm.Targets[0].InstallCommand.ExecuteAsync(null);

        await vm.RelaunchElevatedCommand.ExecuteAsync(null);

        Assert.Equal(["Relaunch"], order);
        Assert.False(shutdown.IsShuttingDown);
        Assert.False(shutdown.QueueDisposed);
        Assert.Contains(banners, b => b.Id == "unlocker-elevation" && b.Kind == BannerKind.Info);
    }

    /// <summary>One thread with a SynchronizationContext and a message pump: the shape of
    /// Avalonia's UI thread. Mirrors MainViewModelLifecycleTests.UiThread exactly.</summary>
    private sealed class UiThread : IDisposable
    {
        private readonly BlockingCollection<Action> _work = new();

        public UiThread()
        {
            Thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new PumpContext(_work));

                foreach (var job in _work.GetConsumingEnumerable()) job();
            })
            { IsBackground = true, Name = "test-ui" };

            Thread.Start();
        }

        public Thread Thread { get; }

        public void Post(Action job) => _work.Add(job);

        public void Dispose() => _work.CompleteAdding();

        private sealed class PumpContext(BlockingCollection<Action> work) : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object? state)
            {
                try
                {
                    work.Add(() => d(state));
                }
                catch (InvalidOperationException)
                {
                    // The pump is closed; the test is over.
                }
            }
        }
    }

    [Fact(Timeout = 15000)]
    public async Task The_operation_does_not_run_on_the_thread_that_started_it()
    {
        var backend = new RecordingUnlockerBackend(Target());
        var vm = Build(backend);
        await vm.RefreshAsync(CancellationToken.None);
        var row = vm.Targets[0];

        using var ui = new UiThread();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ui.Post(() => _ = RunOn(row, started));

        await started.Task;

        // The pair: it ran at all, and not there. Asserting only the thread would also hold for
        // an operation that never ran, whose recorded id is zero and equals nothing.
        Assert.Contains("Install:/clients/ea", backend.Calls);
        Assert.NotEqual(0, backend.OperationThreadId);
        Assert.NotEqual(ui.Thread.ManagedThreadId, backend.OperationThreadId);
    }

    private static async Task RunOn(UnlockerTargetViewModel row, TaskCompletionSource started)
    {
        try
        {
            await row.InstallCommand.ExecuteAsync(null);
            started.TrySetResult();
        }
        catch (Exception e)
        {
            started.TrySetException(e);
        }
    }

    // ---- pair: the shell is not closed on a DECLINED prompt, and is on an accepted one (above). ----
    [Fact]
    public async Task Declined_relaunch_does_not_close_the_shell()
    {
        var order = new List<string>();
        var host = new FakeUnlockerHost(order) { RelaunchAccepted = false };
        var backend = new RecordingUnlockerBackend(Target()) { NextResult = UnlockerResult.NeedsElevation() };
        var vm = Build(backend, host: host, exit: () => order.Add("Exit"));
        await vm.RefreshAsync(CancellationToken.None);
        await vm.Targets[0].InstallCommand.ExecuteAsync(null);

        await vm.RelaunchElevatedCommand.ExecuteAsync(null);

        Assert.Equal(["Relaunch"], order);
    }

    [Fact]
    public async Task DrainAsync_completes_only_once_the_operation_has()
    {
        var backend = new RecordingUnlockerBackend(Target());
        var hold = new TaskCompletionSource();
        backend.Hold = hold;
        var vm = Build(backend);
        await vm.RefreshAsync(CancellationToken.None);

        var install = vm.Targets[0].InstallCommand.ExecuteAsync(null);
        var drain = vm.DrainAsync();

        // The pair: it is still waiting while the operation runs AND it finishes when the operation
        // does. Asserting only the second would pass for a DrainAsync that never waited at all.
        Assert.False(drain.IsCompleted);
        hold.SetResult();
        await install;
        Assert.True(drain.IsCompleted);
    }
}

/// <summary>
/// Every test above builds a bare <see cref="UnlockerViewModel"/>, so none of them notice if
/// <see cref="MainViewModel"/> wired <c>shutdown</c> or <c>banner</c> to a no-op instead of to
/// <c>ShutdownAsync</c>/<c>Raise</c>: the delegate types still match, so it still compiles and
/// every other test still passes. These two drive a REAL <see cref="MainViewModel"/> built
/// through <see cref="TestHost"/>, with a recording backend substituted for
/// <see cref="AppServices.Unlocker"/>, so the wiring itself is what is under test.
/// </summary>
public class MainViewModelUnlockerWiringTests
{
    private static UnlockerTarget Target() =>
        new("test-backend", ClientKind.EaApp, "/clients/ea", "EA app");

    [Fact(Timeout = 15000)]
    public async Task RelaunchElevatedCommand_reaches_the_real_ShutdownAsync()
    {
        var backend = new RecordingUnlockerBackend(Target()) { NextResult = UnlockerResult.NeedsElevation() };
        var host = new FakeUnlockerHost { RelaunchAccepted = true };

        var vm = TestHost.ViewModel(out var hostFixture,
            unlockerService: new UnlockerService([backend]),
            unlockerHost: host,
            unlockerAssets: new StubUnlockerAssets());
        using var _h = hostFixture;

        await vm.Unlocker.RefreshAsync(CancellationToken.None);
        await vm.Unlocker.Targets[0].InstallCommand.ExecuteAsync(null);
        Assert.True(vm.Unlocker.RelaunchElevatedCommand.CanExecute(null));

        await vm.Unlocker.RelaunchElevatedCommand.ExecuteAsync(null);

        // Both halves, per the review: IsShuttingDown alone could be set by something unrelated,
        // and the queue's recorded calls alone would not prove WHICH shutdown ran. Together they
        // prove the injected delegate reached MainViewModel's own ShutdownAsync.
        Assert.True(vm.IsShuttingDown);
        Assert.Equal(["CancelAll", "Dispose"], hostFixture.Queue.Calls);
        Assert.Contains("Relaunch", host.Calls);
    }

    [Fact(Timeout = 15000)]
    public async Task NeedsElevation_result_reaches_the_real_Raise()
    {
        var backend = new RecordingUnlockerBackend(Target()) { NextResult = UnlockerResult.NeedsElevation() };

        var vm = TestHost.ViewModel(out var hostFixture,
            unlockerService: new UnlockerService([backend]),
            unlockerAssets: new StubUnlockerAssets());
        using var _h = hostFixture;

        await vm.Unlocker.RefreshAsync(CancellationToken.None);

        await vm.Unlocker.Targets[0].InstallCommand.ExecuteAsync(null);

        // Only a banner that reached MainViewModel.Banners through the real Raise proves the
        // wiring; Raise dismissing by id is also what keeps a second elevation report from
        // stacking a duplicate.
        Assert.Contains(vm.Banners, b => b.Id == "unlocker-elevation" && b.Kind == BannerKind.Warning);
    }

    /// <summary>
    /// Nothing in the shipped application called RefreshAsync at all, so on a real Windows machine
    /// with a supported client installed, IsSupported was true and Targets stayed empty forever:
    /// a DLC Unlocker section with rows that never appear. Calls StartAsync and never RefreshAsync
    /// directly, because what is under test is that startup itself does the refresh; a test that
    /// called RefreshAsync itself could not have caught this.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task StartAsync_refreshes_the_unlocker_so_a_supported_client_has_a_row_at_startup()
    {
        var target = Target();
        var backend = new RecordingUnlockerBackend(target);

        var vm = TestHost.ViewModel(out var hostFixture, unlockerService: new UnlockerService([backend]));
        using var _h = hostFixture;

        await vm.StartAsync(CancellationToken.None);

        // The pair: a row count alone would also pass a refresh that invented an empty row from
        // nowhere. Only the ClientPath proves the row came from THIS target's detection.
        var row = Assert.Single(vm.Unlocker.Targets);
        Assert.Equal(target.ClientPath, row.ClientPath);
    }
}
