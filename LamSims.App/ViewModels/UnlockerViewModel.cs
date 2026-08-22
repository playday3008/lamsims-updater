using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LamSims.App.Services;
using LamSims.Core.Unlocking;

namespace LamSims.App.ViewModels;

public sealed partial class UnlockerTargetViewModel(
    UnlockerTarget target,
    Func<UnlockerTarget, Task> install,
    Func<UnlockerTarget, Task> remove) : ObservableObject
{
    public UnlockerTarget Target { get; } = target;
    public string DisplayName => Target.DisplayName;
    public string ClientPath => Target.ClientPath;

    [ObservableProperty]
    private string _statusText = "Checking…";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// The commands pass this row's own target. A row that reached for the collection's first
    /// target would call the right method for the wrong client.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task Install() => install(Target);

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task Remove() => remove(Target);

    private bool CanAct() => !IsBusy;
}

/// <summary>
/// The unlocker region.
///
/// RelaunchElevatedCommand lives here rather than on MainViewModel because the button sits inside a
/// region with its own DataContext, so a command declared on MainViewModel would bind by bare name
/// against this type and be silently dead. MainViewModel owns shutdown, so it supplies the
/// behaviour as a Func&lt;Task&gt;.
/// </summary>
public sealed partial class UnlockerViewModel(
    UnlockerService service,
    IUnlockerAssetSource assets,
    IUnlockerHost host,
    IUiDispatcher dispatcher,
    Func<Task> shutdown,
    Action exit,
    Action<Banner> banner) : ObservableObject
{
    public bool IsSupported => service.IsSupported;

    public ObservableCollection<UnlockerTargetViewModel> Targets { get; } = [];

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _currentStep;

    [ObservableProperty]
    private int _completed;

    [ObservableProperty]
    private int _total;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RelaunchElevatedCommand))]
    private bool _requiresElevation;

    public async Task RefreshAsync(CancellationToken ct)
    {
        Targets.Clear();
        if (!IsSupported) return;

        foreach (var target in await service.DetectAllAsync(ct))
        {
            var row = new UnlockerTargetViewModel(target, RunInstallAsync, RunRemoveAsync);
            row.StatusText = Describe(await service.GetStatusAsync(target, ct));
            Targets.Add(row);
        }
    }

    /// <summary>
    /// The operation in flight, so shutdown can wait for it. It is not cancellable (see RunAsync),
    /// and exiting the process midway through step 7 leaves the client a half-written version.dll it
    /// cannot load, which detection then reports as "Installed".
    /// </summary>
    private Task _operation = Task.CompletedTask;

    /// <summary>Completes when no unlocker operation is in flight.</summary>
    public Task DrainAsync() => _operation;

    private Task RunInstallAsync(UnlockerTarget target) =>
        _operation = RunAsync(target, (progress, ct) => service.InstallAsync(target, assets, progress, ct));

    private Task RunRemoveAsync(UnlockerTarget target) =>
        _operation = RunAsync(target, (progress, ct) => service.RemoveAsync(target, progress, ct));

    private async Task RunAsync(UnlockerTarget target,
        Func<IProgress<UnlockerProgress>, CancellationToken, Task<UnlockerResult>> operation)
    {
        if (IsBusy) return;
        SetBusy(true);
        RequiresElevation = false;

        // Cleared, not left: the row shows the previous run's "Done 9/9" until the first report of
        // this one arrives, so a second install opens on a finished-looking progress bar.
        CurrentStep = null;
        Completed = 0;
        Total = 0;

        // Not Progress<T>: it captures a SynchronizationContext at construction and posts to it, so
        // wrapping dispatcher.Post inside one marshals twice, and under xunit, where there is no
        // context, the callbacks land on the thread pool and the assertions after the awaited
        // operation become racy. IUiDispatcher is deterministic in tests and correct in the app.
        var progress = new DispatchedProgress(dispatcher, p =>
        {
            CurrentStep = p.Step;
            Completed = p.Completed;
            Total = p.Total;
        });

        try
        {
            // Off the UI thread: nothing in either project calls ConfigureAwait, so without
            // Task.Run the filesystem work would run on the thread that draws the window. The
            // token is deliberately not passed to Task.Run.
            var result = await Task.Run(() => operation(progress, CancellationToken.None));

            if (result.RequiresElevation)
            {
                RequiresElevation = true;
                banner(new Banner("unlocker-elevation",
                    "The unlocker needs administrator rights. Nothing has been changed.",
                    BannerKind.Warning));
            }
            else if (!result.Success)
            {
                banner(new Banner("unlocker-error",
                    result.Error ?? "The unlocker operation failed.", BannerKind.Error));
            }
            else
            {
                // An EA app install normally ends with exactly one warning, because machine.ini
                // does not exist until EA Desktop has run once. Removal can carry warnings on
                // every step but the DLL delete, just as installation can.
                foreach (var warning in result.Warnings ?? [])
                    banner(new Banner("unlocker-warning", warning, BannerKind.Warning));
            }

            var row = Targets.FirstOrDefault(t => t.Target == target);
            if (row is not null)
                row.StatusText = Describe(
                    await service.GetStatusAsync(target, CancellationToken.None));
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Host first, shutdown second. MainViewModel.ShutdownAsync is one-way and the window greys
    /// itself on IsShuttingDown, so shutting down before the prompt would leave a declined prompt
    /// with a live window, a disposed queue and no controls.
    /// </summary>
    [RelayCommand(CanExecute = nameof(RequiresElevation))]
    private async Task RelaunchElevated()
    {
        if (!host.TryRelaunchElevated())
        {
            banner(new Banner("unlocker-elevation",
                "The elevation prompt was declined, so nothing has changed.", BannerKind.Info));
            return;
        }

        await shutdown();

        // Shutdown flushes and disposes; it does not close the window, and the window greys itself
        // on IsShuttingDown. Without this the accepted prompt leaves a dead shell beside the
        // elevated instance.
        exit();
    }

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        foreach (var row in Targets)
        {
            row.IsBusy = busy;
            row.InstallCommand.NotifyCanExecuteChanged();
            row.RemoveCommand.NotifyCanExecuteChanged();
        }
    }

    private sealed class DispatchedProgress(IUiDispatcher dispatcher, Action<UnlockerProgress> apply)
        : IProgress<UnlockerProgress>
    {
        public void Report(UnlockerProgress value) => dispatcher.Post(() => apply(value));
    }

    private static string Describe(UnlockerStatus status) => status.State switch
    {
        UnlockerState.Installed => "Installed",
        UnlockerState.NotInstalled => "Not installed",
        _ => status.Detail ?? "Unknown",
    };
}
