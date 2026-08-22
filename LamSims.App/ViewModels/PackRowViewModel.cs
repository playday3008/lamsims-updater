using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// The terminal state a scan most recently retired. It latches: while it is set, every
    /// arriving snapshot carrying that same state is ignored. It clears when a different state
    /// arrives, which a genuine re-enqueue always produces because Reset publishes Queued first.
    /// See ApplyQueue.
    /// </summary>
    private QueueItemState? _retiredState;

    /// <summary>The queue's own verdict that it will not run this item again by itself.</summary>
    private bool _isFinal;

    /// <summary>Null when the pack is not in the queue, or when a scan cleared a terminal overlay.</summary>
    public QueueItemState? QueueState { get; private set; }

    /// <summary>
    /// Blocked is offered as well as absent: <c>PackQueue.Enqueue</c> accepts a re-enqueue of a
    /// blocked item, and MarkBlocked's text tells the user to do exactly that on both the first
    /// block and the second. The checkbox is the only way to take it.
    /// </summary>
    public bool IsCheckable =>
        (QueueState is null || QueueState is QueueItemState.Blocked)
        && (ForceCheckable || InstallState != PackInstallState.Installed);

    public bool IsBusy => QueueState
        is QueueItemState.Verifying or QueueItemState.Downloading or QueueItemState.Installing;

    public bool IsProgressVisible => IsBusy && _totalBytes > 0;

    public double ProgressPercent => _totalBytes > 0 ? _bytesCompleted * 100d / _totalBytes : 0;

    /// <summary>Blanked on every terminal state: the snapshot's field is sticky.</summary>
    public string? CurrentEntry => IsTerminal(QueueState) ? null : _entry;

    public bool CanCancel => QueueState
        is QueueItemState.Queued or QueueItemState.Verifying
        or QueueItemState.Downloading or QueueItemState.Installing;

    // Blocked offers neither, and IsFinal does not change that: Cancel and Remove both refuse a
    // pack the queue is not running, so either control would silently no-op. Re-enqueueing is the
    // escape core honours, and IsCheckable is where it is offered.
    public bool CanRemove => QueueState is QueueItemState.Queued;

    // Failed is tested before the warnings arm, and that order matters. PackWorkflow adds the
    // quarantine warning inside ClassifyAsync and returns it alongside a failed download, and
    // PackQueue.Settle writes Warnings, Failed and Error together. With the arms the other way
    // round a quarantined archive whose re-download 404s reads as a warning carrying the
    // quarantine text, and the reason it failed is never shown. The warnings still follow the
    // error rather than being dropped.
    public string? Message => QueueState switch
    {
        QueueItemState.Blocked => null,
        null => _keptMessage,
        QueueItemState.Failed => FailedMessage,
        _ when _warnings.Count > 0 => string.Join(" ", _warnings),
        _ => null,
    };

    private string? FailedMessage =>
        _error is null
            ? (_warnings.Count > 0 ? string.Join(" ", _warnings) : null)
            : string.Join(" ", _warnings.Prepend(_error));

    public RowMessageKind MessageKind => QueueState switch
    {
        QueueItemState.Blocked => RowMessageKind.None,
        null => _keptKind,
        QueueItemState.Failed => RowMessageKind.Error,
        _ when _warnings.Count > 0 => RowMessageKind.Warning,
        _ => RowMessageKind.None,
    };

    public bool IsWarning => MessageKind is RowMessageKind.Warning;

    public bool IsError => MessageKind is RowMessageKind.Error;

    public string StatusText => QueueState switch
    {
        QueueItemState.Queued => "Queued",
        QueueItemState.Verifying => "Verifying…",
        QueueItemState.Downloading => $"Downloading  {ProgressPercent:0}%{RateText}{EtaText}",
        QueueItemState.Installing => $"Installing  {ProgressPercent:0}%",
        // Core's text, verbatim, with nothing invented behind it. MarkBlocked sets
        // Error on every path that produces Blocked, so the null case is unreachable; where it
        // is not, an empty row is honest and a sentence this application made up is not.
        QueueItemState.Blocked => _error ?? string.Empty,
        QueueItemState.Failed => "Failed",
        QueueItemState.Cancelled => "Cancelled",
        QueueItemState.Completed => "Installed",
        _ => ScanText,
    };

    private string RateText => _bytesPerSecond > 0 ? $"  {FormatBytes((long)_bytesPerSecond)}/s" : "";

    private string EtaText => _eta is { } eta ? $"  {FormatEta(eta)}" : "";

    public void ApplyQueue(QueueItemSnapshot snapshot)
    {
        // The queue re-publishes every item wholesale whenever its own state moves (Running to
        // Idle once nothing is left to run, in PackQueue's LoopAsync/TakeNext), not only when an
        // item's own state changes, so a terminal item's snapshot echoes at least once after the
        // publish that carried its ending. A scan reads that ending and retires the overlay
        // (ApplyScan, below); without this guard the echo reapplies the state the scan cleared.
        //
        // The guard latches: it suppresses every snapshot carrying the retired state, because
        // the queue keeps republishing that terminal item for the rest of the session. It is
        // released when a different state arrives (Queued from a re-enqueue), so a real second
        // run is never suppressed.
        if (_retiredState == snapshot.State) return;
        _retiredState = null;

        QueueState = snapshot.State;
        _bytesCompleted = snapshot.BytesCompleted;
        _totalBytes = snapshot.TotalBytes;
        _bytesPerSecond = snapshot.BytesPerSecond;
        _eta = snapshot.Eta;
        _entry = snapshot.CurrentEntry;
        _error = snapshot.Error;
        _warnings = snapshot.Warnings;
        _isFinal = snapshot.IsFinal;
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
        // The bridge calls this on every row absent from every update, and every update carries
        // every item, so with a 30-pack catalog and one download running this is thousands of
        // no-op RaiseAll()s per second on the UI thread. These four are the whole of what this
        // method clears: QueueState is null exactly when DropOverlay has already run (or never
        // needed to), and DropOverlay's other fields are only ever written beside it.
        if (QueueState is null && _retiredState is null
            && _keptMessage is null && _keptKind == RowMessageKind.None)
        {
            return;
        }

        DropOverlay();
        _keptMessage = null;
        _keptKind = RowMessageKind.None;
        _retiredState = null;
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
        _isFinal = false;
    }

    private static bool IsTerminal(QueueItemState? state) =>
        state is QueueItemState.Completed or QueueItemState.Failed or QueueItemState.Cancelled;

    private bool IsFinished => IsTerminal(QueueState) || _isFinal;

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
        if (IsFinished)
        {
            _keptMessage = Message;
            _keptKind = MessageKind;
            _retiredState = QueueState;
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
            nameof(Message), nameof(MessageKind), nameof(IsWarning), nameof(IsError),
            nameof(CurrentEntry), nameof(CanCancel), nameof(CanRemove),
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
