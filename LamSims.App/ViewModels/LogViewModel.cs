using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LamSims.App.Services;

namespace LamSims.App.ViewModels;

/// <summary>
/// The log the window shows. Every member here is touched on the UI thread only: the relay is
/// what crosses threads, and Drain runs inside the dispatcher post it asked for.
/// </summary>
public sealed partial class LogViewModel : ObservableObject
{
    /// <summary>
    /// A bound ring, not upstream's growing string. A long session with sixteen connections
    /// reporting is unbounded otherwise, and the collection is rendered.
    /// </summary>
    public const int MaxLines = 2000;

    private readonly LogRelay _relay;
    private readonly IClipboardService _clipboard;

    /// <summary>
    /// Every call to <see cref="Drain"/> — the top-level one from the constructor, one reached
    /// through <see cref="LogRelay.OnAvailable"/> on whatever thread wrote, or a same-stack
    /// re-entry <see cref="LogRelay.ReleaseWake"/>'s synchronous self-heal causes — increments this
    /// exactly once via <see cref="Interlocked.Increment(ref int)"/> before doing anything else.
    /// That single atomic operation both records "a wake happened" and decides ownership in one
    /// step: whichever call observes the count go 0 → 1 owns the loop; every other call, whatever
    /// the count it observes, has already left its mark in the same counter and can return
    /// immediately. There is deliberately no second field recording a miss. The design this
    /// replaces was a single <c>_draining</c> flag taken with <see cref="Interlocked.Exchange(ref int, int)"/>
    /// and released in a <c>finally</c>: a losing call there simply returned with nothing recorded
    /// at all, and the <c>finally</c> released the flag unconditionally regardless of whether a
    /// write had raced in during the return — so a write that lost that race had no signal for the
    /// owner to ever notice, and the relay's wake flag was left permanently spent with nobody
    /// scheduled to look again. One atomic counter removes the need for a second field entirely:
    /// the increment itself IS the record, for every call including the owner's own, so there is
    /// nothing that can be released without first being seen.
    ///
    /// The owner drains, then decrements; while the result is non-zero, something incremented the
    /// counter since the pass started (or during it), so it loops and drains again rather than
    /// exit with an unredeemed wake. Only when a decrement lands on exactly zero — meaning every
    /// increment observed so far has a matching pass — does the loop stop.
    /// </summary>
    private int _pendingWakes;

    public LogViewModel(LogRelay relay, IUiDispatcher dispatcher, IClipboardService clipboard)
    {
        _relay = relay;
        _clipboard = clipboard;
        _relay.OnAvailable = () => dispatcher.Post(Drain);

        // A line written before this constructor ran is queued but raised no wake (LogRelay only
        // takes the wake flag once a handler is attached), so it would otherwise sit invisible
        // until some unrelated later write happened to surface it. Draining once here, right after
        // OnAvailable is attached, picks up anything already queued.
        Drain();
    }

    public ObservableCollection<LogEntry> Lines { get; } = new();

    public bool IsEmpty => Lines.Count == 0;

    /// <summary>
    /// The invariant: a wake <see cref="LogRelay"/> has already spent is never left with nobody
    /// scheduled to redeem it. See <see cref="_pendingWakes"/> for how the increment-and-check is
    /// kept atomic so that invariant actually holds under real concurrent producers, not merely
    /// under same-stack re-entrancy.
    ///
    /// <see cref="DrainOnce"/> can throw — <see cref="Lines"/> is a plain
    /// <see cref="ObservableCollection{T}"/>, and its <c>CollectionChanged</c> event runs
    /// synchronously into whatever is bound to it (an Avalonia control that itself throws, or the
    /// collection's own reentrancy guard). The <c>try</c>/<c>catch</c> here — around the call, not
    /// a blanket <c>finally</c> that would reset <see cref="_pendingWakes"/> unconditionally and
    /// silently drop whatever it was mid-counting — is what keeps the decrement below on the
    /// throwing path too, so ownership still hands on correctly instead of leaving the counter
    /// stuck above zero forever (which would make every later call return at the increment check
    /// above, deafening the view for the rest of the process).
    /// </summary>
    private void Drain()
    {
        if (Interlocked.Increment(ref _pendingWakes) != 1) return;

        do
        {
            try
            {
                DrainOnce();
            }
            catch (Exception ex)
            {
                // Swallowing this outright would hide a real defect, so it goes where a developer
                // will at least see it. There is nowhere safer to report it through: this loop
                // drains the very log a failure here would otherwise want to write a line to, and
                // rethrowing would either crash the process (a synchronous dispatcher) or vanish
                // into LogRelay.TryWake's own catch-all (an asynchronous one) — neither tells
                // anyone anything the write below doesn't already.
                // Trace, not Debug: Debug.WriteLine is [Conditional("DEBUG")] and compiles away
                // entirely in a Release build, which would leave a drain failure with no trace
                // anywhere and the log permanently, silently quiet.
                Trace.WriteLine($"LogViewModel.Drain: DrainOnce threw: {ex}");
            }
        } while (Interlocked.Decrement(ref _pendingWakes) != 0);

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>One pass: drain whatever is queued, trimming as each line lands rather than after
    /// the whole pass — a 100k-line burst must never balloon <see cref="Lines"/> to 100k entries
    /// before the cap is applied, since the cap exists precisely to bound what a burst can do to
    /// the collection a rendered view is bound to. Only ever called by the single call that owns
    /// <see cref="_pendingWakes"/>'s loop at a time, so mutating <see cref="Lines"/> here is never
    /// concurrent with another call doing the same.
    ///
    /// <see cref="LogRelay.ReleaseWake"/> is in a <c>finally</c> so it runs even if
    /// <see cref="Lines"/> throws partway through the dequeue loop: without it, a throw here would
    /// skip <c>ReleaseWake</c> entirely, leaving the relay's own wake flag stuck taken — a second,
    /// independent way to go permanently deaf that <see cref="Drain"/>'s own exception handling
    /// does not by itself prevent, since it only protects <see cref="_pendingWakes"/>.</summary>
    private void DrainOnce()
    {
        try
        {
            while (_relay.TryDequeue(out var entry))
            {
                Lines.Add(entry);
                if (Lines.Count > MaxLines) Lines.RemoveAt(0);
            }
        }
        finally
        {
            _relay.ReleaseWake();
        }
    }

    [RelayCommand]
    private void Clear()
    {
        Lines.Clear();
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// Spec §8 rules out a file log, which makes this the only way a user can get the log off
    /// their machine to report a bug. Async because Avalonia's clipboard is asynchronous by
    /// nature (IClipboard.SetTextAsync); the command still surfaces on the view model as
    /// <c>CopyCommand</c>, the same way every other *Async command in this codebase does.
    /// </summary>
    [RelayCommand]
    private async Task CopyAsync()
    {
        var text = string.Join(Environment.NewLine, Lines.Select(FormatLine));
        await _clipboard.SetTextAsync(text);
    }

    private static string FormatLine(LogEntry entry) =>
        entry.Code is null ? $"{entry.Time}  {entry.Text}" : $"{entry.Time}  {entry.Code}  {entry.Text}";
}
