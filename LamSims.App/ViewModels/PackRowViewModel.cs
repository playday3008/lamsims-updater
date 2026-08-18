using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LamSims.App.Services;
using LamSims.Core.Catalogs;
using LamSims.Core.Queueing;
using LamSims.Core.Scanning;

namespace LamSims.App.ViewModels;

public enum RowMessageKind { None, Warning, Error }

public sealed partial class PackRowViewModel : ObservableObject
{
    private readonly IQueueController _queue;
    private RelayCommand? _cancelCommand;
    private RelayCommand? _removeCommand;

    public PackRowViewModel(PackEntry entry, IQueueController queue)
    {
        Entry = entry;
        _queue = queue;
    }

    public PackEntry Entry { get; }

    public string Code => Entry.Code;

    public string Name => Entry.Name;

    public string SizeText => FormatBytes(Entry.Size);

    public PackInstallState InstallState { get; private set; } = PackInstallState.NotInstalled;

    public IReadOnlyList<string> MissingDirs { get; private set; } = Array.Empty<string>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCheckable))]
    private bool _forceCheckable;

    [ObservableProperty]
    private bool _isChecked;

    private long _bytesCompleted;
    private long _totalBytes;
    private double _bytesPerSecond;
    private TimeSpan? _eta;
    private string? _entry;
    private string? _error;
    private IReadOnlyList<string> _warnings = Array.Empty<string>();
    private string? _keptMessage;
    private RowMessageKind _keptKind = RowMessageKind.None;

    /// <summary>Null when the pack is not in the queue, or when a scan cleared a terminal overlay.</summary>
    public QueueItemState? QueueState { get; private set; }

    public bool IsCheckable =>
        QueueState is null && (ForceCheckable || InstallState != PackInstallState.Installed);

    public bool IsBusy => QueueState
        is QueueItemState.Verifying or QueueItemState.Downloading or QueueItemState.Installing;

    public bool IsProgressVisible => IsBusy && _totalBytes > 0;

    public double ProgressPercent => _totalBytes > 0 ? _bytesCompleted * 100d / _totalBytes : 0;

    /// <summary>Blanked on every terminal state: the snapshot's field is sticky.</summary>
    public string? CurrentEntry => IsTerminal(QueueState) ? null : _entry;

    public bool CanCancel => QueueState
        is QueueItemState.Queued or QueueItemState.Verifying
        or QueueItemState.Downloading or QueueItemState.Installing;

    // Blocked offers neither: a twice-blocked pack is terminal and a once-blocked one is not,
    // and nothing in the snapshot distinguishes them, so both controls would silently no-op.
    public bool CanRemove => QueueState is QueueItemState.Queued;

    public string? Message => QueueState switch
    {
        QueueItemState.Blocked => null,
        null => _keptMessage,
        _ when _warnings.Count > 0 => string.Join(" ", _warnings),
        QueueItemState.Failed => _error,
        _ => null,
    };

    public RowMessageKind MessageKind => QueueState switch
    {
        QueueItemState.Blocked => RowMessageKind.None,
        null => _keptKind,
        _ when _warnings.Count > 0 => RowMessageKind.Warning,
        QueueItemState.Failed => RowMessageKind.Error,
        _ => RowMessageKind.None,
    };

    public string StatusText => QueueState switch
    {
        QueueItemState.Queued => "Queued",
        QueueItemState.Verifying => "Verifying…",
        QueueItemState.Downloading => $"Downloading  {ProgressPercent:0}%{RateText}{EtaText}",
        QueueItemState.Installing => $"Installing  {ProgressPercent:0}%",
        QueueItemState.Blocked => _error ?? "In use by another copy of this application",
        QueueItemState.Failed => "Failed",
        QueueItemState.Cancelled => "Cancelled",
        QueueItemState.Completed => "Installed",
        _ => ScanText,
    };

    private string RateText => _bytesPerSecond > 0 ? $"  {FormatBytes((long)_bytesPerSecond)}/s" : "";

    private string EtaText => _eta is { } eta ? $"  {FormatEta(eta)}" : "";

    public void ApplyQueue(QueueItemSnapshot snapshot)
    {
        QueueState = snapshot.State;
        _bytesCompleted = snapshot.BytesCompleted;
        _totalBytes = snapshot.TotalBytes;
        _bytesPerSecond = snapshot.BytesPerSecond;
        _eta = snapshot.Eta;
        _entry = snapshot.CurrentEntry;
        _error = snapshot.Error;
        _warnings = snapshot.Warnings;
        _keptMessage = null;
        _keptKind = RowMessageKind.None;

        if (!IsCheckable) IsChecked = false;

        RaiseAll();
    }

    /// <summary>
    /// The pack is absent from the queue's update, either removed or never enqueued. Unlike the
    /// scan's clear this drops the message too: an absent item has no history to explain.
    /// </summary>
    public void ClearQueueOverlay()
    {
        DropOverlay();
        _keptMessage = null;
        _keptKind = RowMessageKind.None;
        RaiseAll();
    }

    private void DropOverlay()
    {
        QueueState = null;
        _bytesCompleted = 0;
        _totalBytes = 0;
        _bytesPerSecond = 0;
        _eta = null;
        _entry = null;
        _error = null;
        _warnings = Array.Empty<string>();
    }

    private static bool IsTerminal(QueueItemState? state) =>
        state is QueueItemState.Completed or QueueItemState.Failed or QueueItemState.Cancelled;

    private static string FormatEta(TimeSpan eta) =>
        eta.TotalHours >= 1 ? $"{(int)eta.TotalHours}h {eta.Minutes}m"
        : eta.TotalMinutes >= 1 ? $"{(int)eta.TotalMinutes}m"
        : $"{eta.Seconds}s";

    public IRelayCommand CancelCommand => _cancelCommand ??=
        new RelayCommand(() => _queue.Cancel(Code), () => CanCancel);

    // The return value is deliberately dropped: Remove refuses the active item, the UI cannot
    // tell which item that is, and the next update says what actually happened.
    public IRelayCommand RemoveCommand => _removeCommand ??=
        new RelayCommand(() => _queue.Remove(Code), () => CanRemove);

    /// <summary>
    /// An `Installed` row's checkbox is cleared and disabled, and this is the only way
    /// past that. It clears the disable for this row and does not check the box. A command
    /// rather than a settable property because a `MenuItem` cannot write one.
    /// </summary>
    [RelayCommand]
    private void Reinstall() => ForceCheckable = true;

    // private, not protected: the class is sealed, and a new protected member in a sealed type
    // is CS0628, which -warnaserror turns into a build failure.
    private string ScanText => InstallState switch
    {
        PackInstallState.Installed => "Installed",
        PackInstallState.InstalledUnverified => "Installed (not verified by this tool)",
        PackInstallState.Partial when MissingDirs.Count == 0 =>
            "Partial — an install started and did not finish",
        PackInstallState.Partial =>
            $"Partial — {MissingDirs.Count} folder{(MissingDirs.Count == 1 ? "" : "s")} missing",
        _ => "Not installed",
    };

    public void ApplyScan(PackScanResult result)
    {
        InstallState = result.State;
        MissingDirs = result.MissingDirs;

        // A terminal row lets go of the queue's word for it at the next scan, so a finished,
        // failed or cancelled pack reads disk state rather than pinning the queue's verdict for
        // the rest of the session. Its message is kept, since the error is what explains the row.
        if (IsTerminal(QueueState))
        {
            _keptMessage = Message;
            _keptKind = MessageKind;
            DropOverlay();
        }

        if (!IsCheckable) IsChecked = false;

        RaiseAll();
    }

    private void RaiseAll()
    {
        foreach (var name in (string[])
        [
            nameof(InstallState), nameof(MissingDirs), nameof(QueueState), nameof(IsCheckable),
            nameof(StatusText), nameof(IsBusy), nameof(ProgressPercent), nameof(IsProgressVisible),
            nameof(Message), nameof(MessageKind), nameof(CurrentEntry), nameof(CanCancel),
            nameof(CanRemove),
        ])
        {
            OnPropertyChanged(name);
        }

        _cancelCommand?.NotifyCanExecuteChanged();
        _removeCommand?.NotifyCanExecuteChanged();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        // InvariantCulture: the tests assert "12.4 GB" and "8.1 MB/s" literally, and on a machine
        // whose culture uses a comma decimal separator the ambient culture would fail them.
        return unit == 0
            ? $"{bytes} B"
            : string.Format(CultureInfo.InvariantCulture, "{0:0.#} {1}", value, units[unit]);
    }
}
