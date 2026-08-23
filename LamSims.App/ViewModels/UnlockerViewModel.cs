using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LamSims.App.Services;
using LamSims.Core.Unlocking;

namespace LamSims.App.ViewModels;

public sealed partial class UnlockerTargetViewModel(UnlockerTarget target) : ObservableObject
{
    public UnlockerTarget Target { get; } = target;
    public string ClientPath => Target.ClientPath;

    /// <summary>
    /// One flat property, not a dotted binding path. MainWindowBindingTests resolves a binding
    /// path as a single member of its data context and fails loudly on anything else, so
    /// {Binding Environment.Describe()} would be reported as unresolved.
    /// </summary>
    public string Title => Target.Environment is { } environment
        ? $"{Target.DisplayName} ({environment.Describe()})"
        : Target.DisplayName;

    [ObservableProperty]
    private string _statusText = "Checking…";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// This row's own warnings. Per row rather than a banner: banners key by id so a repeated
    /// cause replaces its predecessor, and in a batch that left only the last target's warning
    /// visible.
    /// </summary>
    [ObservableProperty]
    private string? _warning;
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

    [ObservableProperty]
    private string? _batchPosition;

    public async Task RefreshAsync(CancellationToken ct)
    {
        foreach (var row in Targets) row.PropertyChanged -= OnRowChanged;
        Targets.Clear();
        if (!IsSupported) return;

        foreach (var target in await service.DetectAllAsync(ct))
        {
            var row = new UnlockerTargetViewModel(target);
            row.StatusText = Describe(await service.GetStatusAsync(target, ct));
            row.PropertyChanged += OnRowChanged;
            Targets.Add(row);
        }

        NotifyBatchCommands();
    }

    /// <summary>
    /// The operation in flight, so shutdown can wait for it. It is not cancellable, and exiting the
    /// process midway through step 7 leaves the client a half-written version.dll it cannot load,
    /// which detection then reports as "Installed".
    /// </summary>
    private Task _operation = Task.CompletedTask;

    /// <summary>Completes when no unlocker operation is in flight.</summary>
    public Task DrainAsync() => _operation;

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

    /// <summary>
    /// The rows own the selection and this type owns the commands, so their CanExecute has to be
    /// re-evaluated when a row is ticked. Without this the buttons stay disabled for ever.
    /// </summary>
    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UnlockerTargetViewModel.IsSelected)) NotifyBatchCommands();
    }

    private void NotifyBatchCommands()
    {
        InstallSelectedCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
    }

    private bool CanRunBatch() => !IsBusy && Targets.Any(t => t.IsSelected);

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var row in Targets) row.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var row in Targets) row.IsSelected = false;
    }

    [RelayCommand(CanExecute = nameof(CanRunBatch))]
    private Task InstallSelected() => Start(
        (target, progress, ct) => service.InstallAsync(target, assets, progress, ct));

    [RelayCommand(CanExecute = nameof(CanRunBatch))]
    private Task RemoveSelected() => Start(
        (target, progress, ct) => service.RemoveAsync(target, progress, ct));

    /// <summary>
    /// Leaves <c>_operation</c> alone when a batch is already in flight. Assigning it the
    /// already-completed task a re-entrant call returns would make DrainAsync, and therefore
    /// shutdown, stop waiting for the batch that is still running.
    /// </summary>
    private Task Start(
        Func<UnlockerTarget, IProgress<UnlockerProgress>, CancellationToken, Task<UnlockerResult>> operation) =>
        IsBusy ? _operation : _operation = RunBatchAsync(operation);

    /// <summary>
    /// Sequential, never parallel: two targets share the unlocker's configuration directory and
    /// the asset cache, and the engine was written for one operation at a time.
    /// </summary>
    private async Task RunBatchAsync(
        Func<UnlockerTarget, IProgress<UnlockerProgress>, CancellationToken, Task<UnlockerResult>> operation)
    {
        if (IsBusy) return;
        var rows = Targets.Where(t => t.IsSelected).ToArray();
        if (rows.Length == 0) return;

        SetBusy(true);
        RequiresElevation = false;
        var succeeded = 0;

        try
        {
            for (var i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                BatchPosition = $"Target {i + 1} of {rows.Length}";
                row.Warning = null;

                // Cleared per target, not per batch: the bar would otherwise show the previous
                // target's finished count until this one's first report arrives.
                CurrentStep = null;
                Completed = 0;
                Total = 0;

                var progress = new DispatchedProgress(dispatcher, p =>
                {
                    CurrentStep = p.Step;
                    Completed = p.Completed;
                    Total = p.Total;
                });

                try
                {
                    var result = await Task.Run(
                        () => operation(row.Target, progress, CancellationToken.None));

                    if (result.RequiresElevation)
                    {
                        // Aborts the batch. Elevation is true of every target on the machine and
                        // the remedy is one relaunch, so continuing would collect the same failure
                        // once per target and then prompt anyway.
                        RequiresElevation = true;
                        row.StatusText = "Administrator rights required";
                        banner(new Banner("unlocker-elevation",
                            "The unlocker needs administrator rights. Nothing has been changed.",
                            BannerKind.Warning));
                        return;
                    }

                    // Hopped, like the detection call in RefreshCoreAsync: GetStatusAsync has no
                    // suspension point on either backend, so awaiting it directly runs the Wine
                    // backend's prefix reopen and registry reads on the thread that draws.
                    row.StatusText = result.Success
                        ? Describe(await Task.Run(
                            () => service.GetStatusAsync(row.Target, CancellationToken.None)))
                        : result.Error ?? "The unlocker operation failed.";
                    if (result.Success) succeeded++;

                    if (result.Warnings is { Count: > 0 } warnings)
                        row.Warning = string.Join(" ", warnings);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Scoped to the row it happened on, the way a returned failure already is: a
                    // prefix that became unreadable mid-batch must not skip the targets queued
                    // behind it, and the summary below must still report what finished. The narrow
                    // catch matches DetectUnlockerAsync, so a bug elsewhere still crashes loudly.
                    row.StatusText = $"The unlocker operation failed: {e.Message}";
                }
            }

            banner(new Banner("unlocker-summary",
                $"{succeeded} of {rows.Length} finished successfully.",
                succeeded == rows.Length ? BannerKind.Info : BannerKind.Warning));
        }
        finally
        {
            BatchPosition = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        foreach (var row in Targets) row.IsBusy = busy;
        NotifyBatchCommands();
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
