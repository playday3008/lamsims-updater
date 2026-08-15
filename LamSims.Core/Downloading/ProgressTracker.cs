using System.Diagnostics;

namespace LamSims.Core.Downloading;

/// <summary>
/// Byte accounting with an exponential moving average of speed.
/// </summary>
public sealed class ProgressTracker
{
    private const double SmoothingFactor = 0.3;

    private readonly string _code;
    private readonly long _totalBytes;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _gate = new();

    private long _bytesCompleted;
    private long _lastBytes;
    private long _lastAccepted = -1;
    private TimeSpan _lastElapsed = TimeSpan.Zero;
    private double _bytesPerSecond;
    private DownloadProgress? _pending;
    private bool _reporting;

    public ProgressTracker(string code, long totalBytes, long alreadyCompletedBytes = 0)
    {
        _code = code;
        _totalBytes = totalBytes;
        _bytesCompleted = alreadyCompletedBytes;
        _lastBytes = alreadyCompletedBytes;
    }

    public long BytesCompleted { get { lock (_gate) return _bytesCompleted; } }

    public void Add(long bytes)
    {
        lock (_gate) _bytesCompleted += bytes;
    }

    public DownloadProgress Snapshot()
    {
        lock (_gate)
        {
            var elapsed = _clock.Elapsed;
            var window = elapsed - _lastElapsed;

            if (window > TimeSpan.Zero)
            {
                var instant = (_bytesCompleted - _lastBytes) / window.TotalSeconds;
                _bytesPerSecond = _bytesPerSecond == 0
                    ? instant
                    : SmoothingFactor * instant + (1 - SmoothingFactor) * _bytesPerSecond;
                _lastBytes = _bytesCompleted;
                _lastElapsed = elapsed;
            }

            var remaining = _totalBytes - _bytesCompleted;
            TimeSpan? eta = _bytesPerSecond > 1 && remaining > 0
                ? TimeSpan.FromSeconds(remaining / _bytesPerSecond)
                : null;

            return new DownloadProgress(_code, _bytesCompleted, _totalBytes, _bytesPerSecond, eta);
        }
    }

    /// <summary>
    /// Hands a snapshot to the caller. Several workers deliver concurrently, so at most one of
    /// them reports at a time and always the newest snapshot it can see: a worker that arrives
    /// while another is reporting leaves its snapshot behind and returns immediately rather
    /// than waiting. The values the caller observes stay non-decreasing, and only the reporting
    /// worker is blocked, so a slow handler cannot stall the transfer.
    ///
    /// Progress is a latest-value signal rather than a stream, so a snapshot overtaken while
    /// another is being reported is dropped rather than queued. Delivering the final one
    /// depends on the reporting worker checking for a newer snapshot and clearing
    /// <c>_reporting</c> in a *single* lock acquisition. Split them and a worker depositing
    /// between the two is neither picked up nor allowed to take over, and the last snapshot of
    /// a download has nothing behind it to re-drive delivery.
    ///
    /// A handler that throws is ignored, because reporting progress must not fail a transfer
    /// that is otherwise healthy.
    /// </summary>
    public void Deliver(IProgress<DownloadProgress>? progress, DownloadProgress snapshot)
    {
        if (progress is null) return;

        lock (_gate)
        {
            if (snapshot.BytesCompleted <= _lastAccepted) return;
            _lastAccepted = snapshot.BytesCompleted;
            _pending = snapshot;

            if (_reporting) return;
            _reporting = true;
        }

        var stoodDown = false;

        try
        {
            while (true)
            {
                DownloadProgress due;

                lock (_gate)
                {
                    if (_pending is null)
                    {
                        _reporting = false;
                        stoodDown = true;
                        return;
                    }

                    due = _pending;
                    _pending = null;
                }

                try
                {
                    progress.Report(due);
                }
                catch (Exception)
                {
                }
            }
        }
        finally
        {
            // A flag left set would silently end delivery for the rest of the download, so it
            // is cleared even on a path nothing is expected to escape through.
            if (!stoodDown)
            {
                lock (_gate) _reporting = false;
            }
        }
    }
}
