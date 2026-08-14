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
    private long _lastDelivered = -1;
    private TimeSpan _lastElapsed = TimeSpan.Zero;
    private double _bytesPerSecond;

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
    /// Hands a snapshot to the caller, dropping it if a later one already went out. Callers
    /// report from several workers and outside any lock of theirs, so deliveries can arrive
    /// out of order; discarding the stale one is what keeps a progress bar from moving
    /// backwards. A handler that throws is ignored: reporting progress must not be able to
    /// fail a transfer that is otherwise healthy.
    /// </summary>
    public void Deliver(IProgress<DownloadProgress>? progress, DownloadProgress snapshot)
    {
        if (progress is null) return;

        lock (_gate)
        {
            if (snapshot.BytesCompleted <= _lastDelivered) return;
            _lastDelivered = snapshot.BytesCompleted;
        }

        try
        {
            progress.Report(snapshot);
        }
        catch (Exception)
        {
        }
    }
}
