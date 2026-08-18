using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LamSims.App.Services;
using LamSims.Core.Catalogs;
using LamSims.Core.Queueing;
using LamSims.Core.Scanning;
using LamSims.Core.Settings;

namespace LamSims.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Func<CancellationToken, Task<CatalogResolution>> _resolve;
    private readonly Func<CatalogSource, CancellationToken, Task<CatalogResolution>> _load;
    private readonly Func<AppSettings, CancellationToken, Task> _save;

    public MainViewModel(
        AppServices services,
        Func<CancellationToken, Task<CatalogResolution>>? resolve = null,
        Func<CatalogSource, CancellationToken, Task<CatalogResolution>>? load = null,
        Func<AppSettings, CancellationToken, Task>? save = null)
    {
        _services = services;
        _resolve = resolve ?? (ct => services.Catalog.ResolveAsync(
            services.CommandLineCatalog, services.Current.CatalogSource, ct));
        _load = load ?? services.Catalog.LoadAsync;
        _save = save ?? services.Settings.SaveAsync;

        // Seeded without going through the property setters: those mark the restart notice and
        // queue a save, and loading a value the user already chose is neither a change nor a
        // reason to warn them.
        _gameDirectory = services.Current.GameDirectory;
        _downloadDirectory = services.Current.DownloadDirectory;
        _connections = services.Current.Connections;

        if (services.SettingsError is { } settingsError)
        {
            Banners.Add(new Banner("settings-load",
                $"Your settings could not be read and defaults are in use: {settingsError}",
                BannerKind.Warning));
        }
    }

    // These wrappers keep the three delegate fields read: a private field assigned and never
    // read is CS0414, and this repository builds warnings as errors. An uncalled private method
    // is not a warning, and CS0414 counts a syntactic read rather than a reachable one.
    private Task<CatalogResolution> ResolveCatalogAsync(CancellationToken ct) => _resolve(ct);

    private Task<CatalogResolution> LoadCatalogFromAsync(CatalogSource source, CancellationToken ct) =>
        _load(source, ct);

    private Task PersistSettingsAsync(CancellationToken ct) => _save(_services.Current, ct);

    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(500);

    private DateTimeOffset? _saveDueAt;

    internal int SettingsWriteCount { get; private set; }

    [ObservableProperty]
    private bool _restartNoticeVisible;

    partial void OnConnectionsChanged(int value)
    {
        _services.Current.Connections = value;
        RestartNoticeVisible = true;
        QueueSave();
    }

    partial void OnDownloadDirectoryChanged(string? value)
    {
        _services.Current.DownloadDirectory = value;
        RestartNoticeVisible = true;
        QueueSave();
    }

    private void QueueSave() => _saveDueAt = _services.Clock.UtcNow + SaveDebounce;

    /// <summary>Writes a pending change only once the debounce has elapsed. Driven by a timer.</summary>
    public Task FlushDueSettingsAsync(CancellationToken ct) =>
        _saveDueAt is { } due && _services.Clock.UtcNow >= due
            ? FlushSettingsNowAsync(ct)
            : Task.CompletedTask;

    /// <summary>Writes a pending change immediately. Used at shutdown and after a picker.</summary>
    public async Task FlushSettingsNowAsync(CancellationToken ct)
    {
        if (_saveDueAt is null) return;

        _saveDueAt = null;

        try
        {
            await PersistSettingsAsync(ct);
            SettingsWriteCount++;
            Dismiss("settings");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Raise(new Banner("settings", $"Your settings could not be saved: {e.Message}", BannerKind.Warning));
        }
    }

    [RelayCommand]
    private async Task BrowseGameAsync(CancellationToken ct)
    {
        var picked = await _services.Pickers.PickFolderAsync("Choose the Sims 4 folder", GameDirectory);
        if (picked is null) return;

        GameDirectory = picked;
        _services.Current.GameDirectory = picked;
        QueueSave();
        await FlushSettingsNowAsync(ct);
        Scan();
    }

    [RelayCommand]
    private async Task BrowseDownloadsAsync(CancellationToken ct)
    {
        var picked = await _services.Pickers.PickFolderAsync("Choose where downloads are kept", DownloadDirectory);
        if (picked is null) return;

        DownloadDirectory = picked;   // its setter queues the save and raises the restart notice
        await FlushSettingsNowAsync(ct);
    }

    public ObservableCollection<Banner> Banners { get; } = new();

    [ObservableProperty]
    private string _emptyStateMessage = "";

    [ObservableProperty]
    private string _catalogDescription = "";

    private CatalogSource? _cachedCopy;

    public async Task LoadCatalogAsync(CancellationToken ct)
    {
        CatalogResolution resolution;

        try
        {
            resolution = await ResolveCatalogAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // The application is closing. Not a catalog error, and not a banner.
            return;
        }

        Apply(resolution);
    }

    private void Apply(CatalogResolution resolution)
    {
        _cachedCopy = resolution.CachedCopy;
        UseCachedCatalogCommand.NotifyCanExecuteChanged();
        Dismiss("catalog");
        Dismiss("catalog-rejected");

        switch (resolution.Status)
        {
            case CatalogStatus.Loaded when resolution.Load is { } load:
                BuildRows(load.Catalog.Packs);
                CatalogDescription = resolution.Source?.Location ?? "";
                EmptyStateMessage = load.Catalog.Packs.Count == 0 ? "This catalog lists no packs." : "";

                if (load.Rejected.Count > 0)
                {
                    Raise(new Banner("catalog-rejected",
                        $"{load.Rejected.Count} catalog entr{(load.Rejected.Count == 1 ? "y was" : "ies were")} "
                        + $"skipped. First: {load.Rejected[0].Description} — {load.Rejected[0].Reason}",
                        BannerKind.Warning));
                }

                break;

            case CatalogStatus.Empty:
                BuildRows([]);
                CatalogDescription = "";
                EmptyStateMessage = "No catalog selected.";
                break;

            default:
                BuildRows([]);
                CatalogDescription = "";
                EmptyStateMessage = "The catalog could not be loaded.";
                Raise(new Banner("catalog", resolution.Error ?? "The catalog could not be loaded.", BannerKind.Error));
                break;
        }

        // Every branch rebuilt the rows, and a fresh row knows nothing about what is on disk.
        // StartAsync scans separately, but ChangeCatalogAsync and UseCachedCatalogAsync reach
        // the rows only through here, so without this an already-installed pack reads
        // "Not installed" with a live checkbox and Add re-downloads it. Scan() is a no-op with
        // no game directory.
        if (!string.IsNullOrWhiteSpace(GameDirectory)) Scan();
    }

    private void Raise(Banner banner)
    {
        Dismiss(banner.Id);
        Banners.Add(banner);
    }

    private void Dismiss(string id)
    {
        for (var i = Banners.Count - 1; i >= 0; i--)
        {
            if (Banners[i].Id == id) Banners.RemoveAt(i);
        }
    }

    [RelayCommand]
    private void DismissBanner(Banner banner) => Banners.Remove(banner);

    /// <summary>
    /// Refused while anything is still in the queue: a rebuilt row list orphans an in-flight
    /// pack, which keeps its lock and its bandwidth with no row to cancel it from, and a pack
    /// whose entry changed would render progress under a size and digest the run is not using.
    /// </summary>
    public bool CanChangeCatalog => PendingCount == 0;

    [RelayCommand(CanExecute = nameof(CanChangeCatalog))]
    private async Task ChangeCatalogAsync(CancellationToken ct)
    {
        var picked = await _services.Pickers.PickFileAsync("Choose a catalog file", _services.Paths.Root);
        if (picked is null) return;

        _services.Current.CatalogSource = picked;
        await SaveSettingsAsync(ct);
        Apply(await LoadCatalogFromAsync(new CatalogSource(CatalogSourceKind.Settings, picked), ct));
    }

    private bool CanUseCachedCatalog => _cachedCopy is not null && PendingCount == 0;

    [RelayCommand(CanExecute = nameof(CanUseCachedCatalog))]
    public async Task UseCachedCatalogAsync(CancellationToken ct)
    {
        if (_cachedCopy is not { } source) return;

        Apply(await LoadCatalogFromAsync(source, ct));
    }

    private Task SaveSettingsAsync(CancellationToken ct)
    {
        QueueSave();
        return FlushSettingsNowAsync(ct);
    }

    public ObservableCollection<PackRowViewModel> Rows { get; } = new();

    public IReadOnlyList<PackRowViewModel> FilteredRows =>
        string.IsNullOrWhiteSpace(SearchText)
            ? Rows
            : Rows.Where(r =>
                r.Code.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                r.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredRows))]
    private string _searchText = "";

    [ObservableProperty]
    private string? _gameDirectory;

    [ObservableProperty]
    private bool _gameDirectoryReadable = true;

    [ObservableProperty]
    private bool _isQueueAlive = true;

    [ObservableProperty]
    private string? _downloadDirectory;

    [ObservableProperty]
    private int _connections = 8;

    public QueueState QueueState { get; private set; } = QueueState.Idle;

    public int PendingCount { get; private set; }

    public int FailedCount { get; private set; }

    public string StatusText => QueueState switch
    {
        QueueState.Running => $"Running — {PendingCount} left{FailedText}",
        QueueState.Pausing => $"Pausing — {PendingCount} left{FailedText}",
        QueueState.Paused => $"Paused — {PendingCount} left{FailedText}",
        _ => PendingCount == 0 && FailedCount == 0 ? "Idle" : $"Idle — {PendingCount} left{FailedText}",
    };

    private string FailedText => FailedCount == 0 ? "" : $", {FailedCount} failed";

    public void BuildRows(IEnumerable<PackEntry> entries)
    {
        Rows.Clear();

        // The rescan dedupe is keyed by code and the rows it described are gone. Left standing,
        // a pack completed before a catalog change and enqueued again afterwards would have its
        // rebuilt row adopt an echoed Completed with no rescan to retire the overlay.
        _rescanned.Clear();

        foreach (var entry in entries) Rows.Add(new PackRowViewModel(entry, _services.Queue));

        OnPropertyChanged(nameof(FilteredRows));
    }

    public PackRowViewModel? RowFor(string code) =>
        Rows.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.Ordinal));

    public void ApplyUpdate(QueueUpdate update)
    {
        PendingCount = update.Items.Count(i => i.State
            is not (QueueItemState.Completed or QueueItemState.Failed or QueueItemState.Cancelled));
        FailedCount = update.Items.Count(i => i.State == QueueItemState.Failed);

        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(FailedCount));

        ChangeCatalogCommand.NotifyCanExecuteChanged();
        UseCachedCatalogCommand.NotifyCanExecuteChanged();

        ApplyQueueState(update.State);

        // Every update carries every item, so a completion echoes for the rest of the session
        // and the set below is what stops it rescanning each time. The set also has to be
        // released: a re-enqueued pack leaves Completed first, and if its code stayed in the set
        // its second completion would fire no rescan, leaving the row pinned at "Installed" with
        // a disabled checkbox and a terminal overlay nothing retires.
        foreach (var item in update.Items)
        {
            if (item.State != QueueItemState.Completed) _rescanned.Remove(item.Code);
        }

        if (update.Items.Any(i => i.State == QueueItemState.Completed && _rescanned.Add(i.Code))) Scan();
    }

    private readonly HashSet<string> _rescanned = new(StringComparer.Ordinal);

    /// <summary>Test-visible: proves the rescan fires once per completion, not once per update.</summary>
    internal int ScanCount { get; private set; }

    [RelayCommand]
    private void Scan()
    {
        if (string.IsNullOrWhiteSpace(GameDirectory)) return;

        ScanCount++;

        // Both calls are pure and total by core's contract (neither writes, and neither throws
        // on a marker's content), so this command has no error path of its own.
        var markers = _services.InstallState.LoadAll(GameDirectory);
        var result = InstallScanner.Scan(GameDirectory, Rows.Select(r => r.Entry), markers);

        GameDirectoryReadable = result.GameDirectoryReadable;

        if (result.GameDirectoryReadable)
        {
            Dismiss("game-directory");
        }
        else
        {
            Raise(new Banner("game-directory",
                $"The game folder '{GameDirectory}' could not be read, so no pack can be shown as installed.",
                BannerKind.Error));
        }

        // Ordinal: InstallScanner builds every PackScanResult from the catalog's own pack.Code,
        // so these strings are identical to the rows'. The store's case-insensitive dictionary
        // is consulted inside the scanner and its comparer never reaches the results.
        foreach (var scan in result.Packs) RowFor(scan.Code)?.ApplyScan(scan);
    }

    public void ApplyQueueState(QueueState state)
    {
        QueueState = state;

        OnPropertyChanged(nameof(QueueState));
        OnPropertyChanged(nameof(StatusText));
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        CancelAllCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var row in FilteredRows.Where(r => r.IsCheckable)) row.IsChecked = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var row in Rows) row.IsChecked = false;
    }

    private bool CanAdd => IsQueueAlive && !string.IsNullOrWhiteSpace(GameDirectory) && GameDirectoryReadable;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddSelected()
    {
        foreach (var row in Rows.Where(r => r.IsChecked && r.IsCheckable).ToList())
        {
            _services.Queue.Enqueue(row.Entry, GameDirectory!);
            row.IsChecked = false;
        }
    }

    private bool CanPause => IsQueueAlive && QueueState == QueueState.Running;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause() => _services.Queue.Pause();

    private bool CanResume => IsQueueAlive && QueueState is QueueState.Paused or QueueState.Pausing;

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume() => _services.Queue.Resume();

    private bool CanCancelAll => IsQueueAlive && PendingCount > 0;

    [RelayCommand(CanExecute = nameof(CanCancelAll))]
    private void CancelAll() => _services.Queue.CancelAll();

    partial void OnGameDirectoryChanged(string? value) => AddSelectedCommand.NotifyCanExecuteChanged();

    partial void OnGameDirectoryReadableChanged(bool value) => AddSelectedCommand.NotifyCanExecuteChanged();

    partial void OnIsQueueAliveChanged(bool value)
    {
        AddSelectedCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        CancelAllCommand.NotifyCanExecuteChanged();
    }

    private QueueBridge? _bridge;
    private Task? _queueRun;
    private Task? _bridgeRun;

    [ObservableProperty]
    private bool _isShuttingDown;

    private bool _started;

    public async Task StartAsync(CancellationToken ct)
    {
        // MainWindow.OnOpened is an `async void` override and is the only caller, so nothing in
        // the framework guarantees it runs once. A second call would build a second QueueBridge
        // over a single-reader channel, and the two would split the updates between them.
        if (_started) return;

        _started = true;

        _bridge = new QueueBridge(
            _services.Queue, _services.Dispatcher, RowFor, () => Rows, ApplyUpdate, NoteQueueClosed);

        _queueRun = ObserveQueueAsync(ct);
        _bridgeRun = _bridge.RunAsync();

        await LoadCatalogAsync(ct);

        if (!string.IsNullOrWhiteSpace(GameDirectory)) Scan();
    }

    /// <summary>
    /// The queue's fault arrives here rather than through the bridge. `RunAsync`'s finally calls
    /// `_updates.Writer.TryComplete()` with no argument on every exit path including the fault
    /// path (`PackQueue.cs:536`, and `:1093` for a queue never run), so the bridge's
    /// `await foreach` always ends cleanly and the `fault` it reports is always null against a
    /// real queue. Left unobserved, the exception surfaces only where `ShutdownAsync` awaits
    /// `_queueRun`, inside `OnClosing`'s `async void`, as an unhandled crash on window close,
    /// and the banner it raises would be dead code.
    /// </summary>
    private async Task ObserveQueueAsync(CancellationToken ct)
    {
        try
        {
            await _services.Queue.RunAsync(ct);
        }
        catch (Exception e)
        {
            // Through the dispatcher: RunAsync's continuation is not on the UI thread.
            _services.Dispatcher.Post(() => NoteQueueClosed(e));
        }
    }

    /// <summary>
    /// The queue has ended. Two callers: the bridge, when the update channel closes (always with
    /// a null fault, see <see cref="ObserveQueueAsync"/>), and <c>ObserveQueueAsync</c> itself,
    /// which is the only place a real fault can be seen. Both cases disable the queue controls;
    /// only a fault also banners.
    /// </summary>
    public void NoteQueueClosed(Exception? fault)
    {
        IsQueueAlive = false;

        if (fault is not null)
        {
            Raise(new Banner("queue",
                $"The download queue stopped and this session cannot start more work: {fault.Message}",
                BannerKind.Error));
        }
    }

    [RelayCommand]
    public async Task ShutdownAsync()
    {
        if (IsShuttingDown) return;

        IsShuttingDown = true;

        // This runs from OnClosing's `async void`, where an escaping exception is an unhandled
        // crash on close, and a crash here is the same mid-extract exit the wait above prevents.
        // FlushSettingsNowAsync catches only IOException and UnauthorizedAccessException, so any
        // other failure from the save delegate (a serialiser fault, say) would escape it.
        try
        {
            await FlushSettingsNowAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            Raise(new Banner("settings", $"Your settings could not be saved: {e.Message}", BannerKind.Warning));
        }

        // CancelAll before disposal, and Complete() never. Complete() makes a blocked pack
        // eligible to start, clears a standing pause and releases the loop's signal, so between
        // it and the next statement the loop can take a pack lock and reach the installer, which
        // writes its journal marker before the first entry and leaves a Partial install behind.
        // CancelAll marks every pending and blocked item Cancelled, which is terminal, and
        // DisposeAsync calls StopAcceptingWork itself, so Complete() adds nothing anyway.
        //
        // Guarded for the same reason as the flush above: DisposeAsync waits for the runner and
        // runs the cancellation callback chain on this thread.
        //
        // The inner finally matters. PackQueue.CancelAll ends in cts.Cancel(), which runs
        // registered cancellation callbacks synchronously on this thread and rethrows them
        // wrapped in an AggregateException. With both calls in one try that throw would skip the
        // disposal: _completed would never be set, the loop would keep taking Queued items and
        // installing packs after the user closed the window, and the window would stay open,
        // held by MainWindow's re-entrancy guard, while it happened. Disposal therefore happens
        // on every path out of CancelAll.
        try
        {
            try
            {
                _services.Queue.CancelAll();
            }
            finally
            {
                await _services.Queue.DisposeAsync();
            }
        }
        catch (Exception e)
        {
            Raise(new Banner("queue",
                $"The download queue did not stop cleanly: {e.Message}", BannerKind.Error));
        }

        // Safe unguarded: ObserveQueueAsync catches the queue's fault and QueueBridge catches
        // its own, so each task always completes successfully.
        if (_queueRun is not null) await _queueRun;
        if (_bridgeRun is not null) await _bridgeRun;
    }
}
