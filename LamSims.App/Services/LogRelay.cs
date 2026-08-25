using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using LamSims.Core.Logging;

namespace LamSims.App.Services;

/// <param name="Time">Pre-rendered "HH:mm:ss". A string rather than a DateTimeOffset because the
/// item template binds ONE member per path and must not run a converter per line.</param>
/// <param name="IsWarning">Bound by `Classes.log-warning`, and a boolean per severity rather than
/// the enum because MainWindowBindingTests resolves each binding path as a single member.</param>
public sealed record LogEntry(string Time, string? Code, string Text, bool IsWarning, bool IsError);

/// <summary>
/// The App's ILogSink. It does the two things Core cannot: stamps the line, and gets it off
/// whatever thread produced it.
///
/// Split from LogViewModel deliberately. Composition.Build assembles the Core services, but both
/// the tests and MainWindow then replace members with `services with { Dispatcher = … }` — so a
/// sink that captured the dispatcher at Build time would hold the one that was thrown away. This
/// half is built early with no dispatcher; the view model attaches to it later with the real one.
/// </summary>
public sealed class LogRelay(IClock clock) : ILogSink
{
    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private int _wakePosted;

    /// <summary>
    /// Raised when lines become available and no wake is outstanding. May be attached after lines
    /// have already been written — see the class doc, which builds this relay before a dispatcher
    /// exists to attach one — so <see cref="Write"/> only takes the wake flag once a handler is
    /// present, and never leaves the flag stuck: a handler that throws (a shut-down dispatcher
    /// during exit, say) has its exception swallowed here and the flag released, because an
    /// <see cref="ILogSink"/> must never break the caller that logged through it.
    /// </summary>
    public Action? OnAvailable { get; set; }

    public void Write(LogLine line)
    {
        var local = clock.UtcNow.ToLocalTime();

        _pending.Enqueue(new LogEntry(
            local.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            line.Code,
            line.Text,
            line.Severity == LogSeverity.Warning,
            line.Severity == LogSeverity.Error));

        TryWake();
    }

    public bool TryDequeue([MaybeNullWhen(false)] out LogEntry entry) => _pending.TryDequeue(out entry);

    /// <summary>
    /// Called by the consumer after it has drained. Correct without relying on the consumer
    /// draining again afterwards: a write can arrive while the flag is still held (it lost the
    /// race in <see cref="TryWake"/> and left its line queued with nobody watching), so this
    /// clears the flag and then re-checks the queue, re-waking if something is still there rather
    /// than depending on a later, unrelated write to surface it.
    /// </summary>
    public void ReleaseWake()
    {
        Interlocked.Exchange(ref _wakePosted, 0);
        if (!_pending.IsEmpty) TryWake();
    }

    /// <summary>
    /// Takes the wake flag and raises <see cref="OnAvailable"/>, at most once per outstanding
    /// wake. Reads the handler into a local first and does nothing when it is null, so a write
    /// that lands before a consumer attaches never takes the flag — leaving it clear for whichever
    /// write follows attachment, instead of taking it once and stalling forever.
    /// </summary>
    private void TryWake()
    {
        var handler = OnAvailable;
        if (handler is null) return;
        if (Interlocked.Exchange(ref _wakePosted, 1) != 0) return;

        try
        {
            handler();
        }
        catch
        {
            // ILogSink must never throw back into its caller — a ChunkFetcher retry loop, say —
            // and a dispatcher can throw while the app is shutting down. Release the flag so a
            // later write still gets a chance once a live consumer is attached.
            Interlocked.Exchange(ref _wakePosted, 0);
        }
    }
}
