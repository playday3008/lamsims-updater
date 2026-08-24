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
using LamSims.Core.Unlocking.Wine;

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
public sealed partial class UnlockerViewModel : ObservableObject
{
    private readonly UnlockerService service;
    private readonly IUnlockerAssetSource assets;
    private readonly IUnlockerHost host;
    private readonly IUiDispatcher dispatcher;
    private readonly Func<Task> shutdown;
    private readonly Action exit;
    private readonly Action<Banner> banner;

    public UnlockerViewModel(
        UnlockerService service,
        IUnlockerAssetSource assets,
        IUnlockerHost host,
        IUiDispatcher dispatcher,
        Func<Task> shutdown,
        Action exit,
        Action<Banner> banner,
        UnlockerNotes? notes = null)
    {
        this.service = service;
        this.assets = assets;
        this.host = host;
        this.dispatcher = dispatcher;
        this.shutdown = shutdown;
        this.exit = exit;
        this.banner = banner;

        // Connected here rather than injected into the relay, because the relay is created before
        // this type exists: the backend reports through it from the first detection, which runs
        // during startup. Dispatched because a note can arrive from the pool thread RunBatchAsync
        // uses, and ObservableCollection raises its change notifications on the calling thread.
        notes?.Connect(message => dispatcher.Post(() =>
        {
            DetectionNotes.Add(message);
            HasDetectionNotes = DetectionNotes.Count > 0;
        }));
    }

    public bool IsSupported => service.IsSupported;

    public ObservableCollection<UnlockerTargetViewModel> Targets { get; } = [];

    /// <summary>
    /// Why detection found nothing, or found less than the user expected. Not banners: there can be
    /// one per prefix and banners key by id, so a repeated cause would replace its predecessor and
    /// the list would collapse to whichever prefix was scanned last.
    /// </summary>
    public ObservableCollection<string> DetectionNotes { get; } = [];

    /// <summary>
    /// One flat property, not a dotted binding path. MainWindowBindingTests resolves a binding path
    /// as a single member of its data context, so {Binding DetectionNotes.Count} would be reported
    /// as unresolved and the block would silently never appear.
    /// </summary>
    [ObservableProperty]
    private bool _hasDetectionNotes;

    /// <summary>
    /// True while detection is running. A batch started then would act on rows that are about to be
    /// replaced, so the commands are disabled for its duration.
    /// </summary>
    [ObservableProperty]
    private bool _isDetecting;

    partial void OnIsDetectingChanged(bool value) => NotifyBatchCommands();

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

    /// <summary>
    /// Refused, visibly, exactly like <see cref="Start"/> refuses a re-entrant batch. Mirrors that
    /// method's shape for the same reason: the guard and the field assignment cannot be one
    /// statement, because a refusal must not overwrite <see cref="_detection"/> with an
    /// already-completed task while a real detection from an earlier call is still in flight — that
    /// would make <see cref="DrainAsync"/> stop waiting for it, the same hazard <see cref="Start"/>'s
    /// own comment describes for <see cref="_operation"/>. Two overlapping detections are not merely
    /// wasteful: the second call's own synchronous Clear() runs while the first is still awaiting
    /// DetectAllAsync, so the first resumes and repopulates the just-cleared list, and the second
    /// then appends its own scan on top without ever re-clearing — every detected target listed
    /// twice, and worse, both loops mutate the same non-thread-safe ObservableCollection at once.
    /// </summary>
    public Task RefreshAsync(CancellationToken ct)
    {
        if (IsBusy || IsDetecting)
        {
            banner(new Banner("unlocker-busy",
                "The unlocker is still checking your clients, so the client list was not refreshed.",
                BannerKind.Info));
            return Task.CompletedTask;
        }

        return _detection = RefreshCoreAsync(ct);
    }

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        IsDetecting = true;
        try
        {
            foreach (var row in Targets) row.PropertyChanged -= OnRowChanged;
            Targets.Clear();
            DetectionNotes.Clear();
            HasDetectionNotes = false;
            if (!IsSupported) return;

            // Task.Run, matching ObserveQueueAsync's idiom in MainViewModel: RefreshAsync is called
            // from MainWindow.OnOpened (via StartAsync) and from a property setter, both on the UI
            // thread, and DetectAllAsync's Wine backend runs a synchronous filesystem walk — the
            // home directory, both XDG roots and up to eight full .reg parses per prefix — with no
            // yield of its own. Without the hop that walk runs on the thread that draws. The token
            // is not passed to Task.Run itself, for the same reason ObserveQueueAsync's comment
            // gives: an already-cancelled token would fault this task rather than let the call
            // return normally.
            foreach (var target in await Task.Run(() => service.DetectAllAsync(ct)))
            {
                var row = new UnlockerTargetViewModel(target);
                row.StatusText = Describe(await Task.Run(() => service.GetStatusAsync(target, ct)));
                row.PropertyChanged += OnRowChanged;
                Targets.Add(row);
            }
        }
        finally
        {
            IsDetecting = false;
            NotifyBatchCommands();
        }
    }

    /// <summary>
    /// The operation in flight, so shutdown can wait for it. It is not cancellable, and exiting the
    /// process midway through step 7 leaves the client a half-written version.dll it cannot load,
    /// which detection then reports as "Installed".
    /// </summary>
    private Task _operation = Task.CompletedTask;

    /// <summary>
    /// The detection in flight, so shutdown can wait for it too. OnWinePrefixChanged re-detects
    /// fire-and-forget, so a scan can be running with nobody awaiting it, and left unwaited a
    /// dispatcher.Post callback it queued — a note, a row update — can land after teardown has
    /// begun.
    /// </summary>
    private Task _detection = Task.CompletedTask;

    /// <summary>
    /// Completes when no unlocker operation or detection is in flight. Returns <c>_operation</c>
    /// itself, unwrapped, whenever detection is already done, rather than always wrapping it in
    /// Task.WhenAll: a wrapper is a fresh Task whose own completion is one more scheduled
    /// continuation away from _operation's own, so a caller that holds the drain task can watch the
    /// batch finish while the drain still reports itself incomplete. Only the rarer case — a
    /// detection genuinely still running — pays for the wrapper.
    /// </summary>
    public Task DrainAsync() => _detection.IsCompleted ? _operation : Task.WhenAll(_detection, _operation);

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

    private bool CanRunBatch() => !IsBusy && !IsDetecting && Targets.Any(t => t.IsSelected);

    /// <summary>
    /// The only route to a re-scan once the window is open: detection otherwise runs only at
    /// startup and on a Wine-prefix change, so a user who plugs in a launcher afterwards, or fixes
    /// a permission problem <see cref="DetectionNotes"/> named, would have no way to ask again
    /// short of restarting. RefreshAsync already refuses re-entrantly and banners, so this needs no
    /// CanExecute of its own.
    /// </summary>
    [RelayCommand]
    private Task Refresh() => RefreshAsync(CancellationToken.None);

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
        // Start is this method's only caller, and its own IsBusy ternary already refuses a
        // re-entrant call before RunBatchAsync is ever reached, so IsBusy can never be true here.
        // Only IsDetecting needs checking: a detection that started after Start's check passed but
        // before this line runs. A second call site would have to preserve that guarantee itself.
        if (IsDetecting)
        {
            banner(new Banner("unlocker-busy",
                "The unlocker is still checking your clients, so nothing was started.",
                BannerKind.Info));
            return;
        }
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

                var progress = new DispatchedProgress<UnlockerProgress>(dispatcher, p =>
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

    private sealed class DispatchedProgress<T>(IUiDispatcher dispatcher, Action<T> apply)
        : IProgress<T>
    {
        public void Report(T value) => dispatcher.Post(() => apply(value));
    }

    private static string Describe(UnlockerStatus status) => status.State switch
    {
        UnlockerState.Installed => "Installed",
        UnlockerState.NotInstalled => "Not installed",
        _ => status.Detail ?? "Unknown",
    };
}
