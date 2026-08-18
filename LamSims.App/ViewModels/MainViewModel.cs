using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LamSims.App.Services;
using LamSims.Core.Catalogs;
using LamSims.Core.Queueing;
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

        // Seeded without going through the property setters: those queue a save and, for the
        // download directory, move the engine. A value the user already chose is neither a
        // change nor a reason to write it back.
        _gameDirectory = services.Current.GameDirectory;
        _downloadDirectory = services.Current.DownloadDirectory;
        _connections = services.Current.Connections;
    }

    // These wrappers keep the three delegate fields read: a private field assigned and never
    // read is CS0414, and this repository builds warnings as errors. An uncalled private method
    // is not a warning, and CS0414 counts a syntactic read rather than a reachable one.
    private Task<CatalogResolution> ResolveCatalogAsync(CancellationToken ct) => _resolve(ct);

    private Task<CatalogResolution> LoadCatalogFromAsync(CatalogSource source, CancellationToken ct) =>
        _load(source, ct);

    private Task PersistSettingsAsync(CancellationToken ct) => _save(_services.Current, ct);

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

        ApplyQueueState(update.State);
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
}
