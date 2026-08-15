using LamSims.Core.Downloading;

namespace LamSims.Core.Tests;

public class ProgressTrackerTests
{
    private static DownloadProgress At(long bytes) => new("EP01", bytes, 1_000_000, 0, null);

    [Fact]
    public void Never_hands_the_caller_a_value_lower_than_one_it_already_handed()
    {
        const int Workers = 8;
        const int PerWorker = 20_000;
        const long Total = Workers * PerWorker;

        var tracker = new ProgressTracker("EP01", Total);
        var next = 0L;

        var highWater = -1L;
        var inversions = 0;
        var gate = new object();

        var progress = new SyncProgress<DownloadProgress>(p =>
        {
            lock (gate)
            {
                if (p.BytesCompleted < highWater) inversions++;
                else highWater = p.BytesCompleted;
            }
        });

        // Eight workers is the engine's default connection count, and this is the interleaving
        // that matters: two workers passing the ordering check in one order and reaching the
        // caller's handler in the other.
        var workers = Enumerable.Range(0, Workers).Select(_ => new Thread(() =>
        {
            for (var i = 0; i < PerWorker; i++)
            {
                var value = Interlocked.Increment(ref next);
                tracker.Deliver(progress, new DownloadProgress("EP01", value, Total, 0, null));
            }
        })).ToArray();

        foreach (var worker in workers) worker.Start();
        foreach (var worker in workers) worker.Join();

        Assert.Equal(0, inversions);
        Assert.Equal(Total, highWater);
    }

    [Fact]
    public void Leaves_an_arriving_worker_free_while_another_reports()
    {
        var tracker = new ProgressTracker("EP01", 1_000_000);
        using var reporterIsInTheHandler = new ManualResetEventSlim(false);
        using var releaseTheHandler = new ManualResetEventSlim(false);

        var gate = new object();
        var observed = new List<long>();
        var inHandler = 0;
        var mostAtOnce = 0;

        var progress = new SyncProgress<DownloadProgress>(p =>
        {
            lock (gate)
            {
                inHandler++;
                if (inHandler > mostAtOnce) mostAtOnce = inHandler;
                observed.Add(p.BytesCompleted);
            }

            // The lock is released before parking, so a second worker entering the handler can
            // still record itself and a regression fails rather than deadlocks. The park also
            // outlasts the arriving worker's join deadline below by a wide margin, so a blocked
            // arriver cannot slip through on this wait expiring first.
            if (p.BytesCompleted == 100)
            {
                reporterIsInTheHandler.Set();
                releaseTheHandler.Wait(TimeSpan.FromSeconds(60));
            }

            lock (gate) inHandler--;
        });

        var reporter = new Thread(() => tracker.Deliver(progress, At(100)));
        reporter.Start();
        Assert.True(reporterIsInTheHandler.Wait(TimeSpan.FromSeconds(10)));

        var arriver = new Thread(() => tracker.Deliver(progress, At(200)));
        arriver.Start();

        // A worker must not wait on another worker's handler; reporting from inside the lock
        // would park this thread instead.
        var arriverWasFree = arriver.Join(TimeSpan.FromSeconds(3));

        releaseTheHandler.Set();
        Assert.True(reporter.Join(TimeSpan.FromSeconds(60)));
        Assert.True(arriver.Join(TimeSpan.FromSeconds(60)));
        Assert.True(arriverWasFree, "the arriving worker waited on the reporting worker's handler");

        lock (gate)
        {
            // One at a time, and the deposited snapshot still went out.
            Assert.Equal(1, mostAtOnce);
            Assert.Equal(new[] { 100L, 200L }, observed);
        }
    }

    [Fact]
    public void Delivers_the_newest_snapshot_however_two_workers_interleave()
    {
        // The newest snapshot must reach the caller whichever order the two workers interleave in.
        // The stand-down invariant's losing window, where a worker deposits between the reporter's
        // last check and its release of the flag, is a few instructions wide and 500 rounds do not
        // hit it; splitting the check and the clear across two lock acquisitions still passes.
        for (var round = 0; round < 500; round++)
        {
            var tracker = new ProgressTracker("EP01", 1_000_000);
            var gate = new object();
            var highest = 0L;

            var progress = new SyncProgress<DownloadProgress>(p =>
            {
                lock (gate)
                {
                    if (p.BytesCompleted > highest) highest = p.BytesCompleted;
                }
            });

            var first = new Thread(() => tracker.Deliver(progress, At(100)));
            var second = new Thread(() => tracker.Deliver(progress, At(200)));

            first.Start();
            second.Start();
            first.Join();
            second.Join();

            lock (gate) Assert.Equal(200, highest);
        }
    }

    [Fact]
    public void Drops_a_snapshot_already_overtaken_by_a_later_one()
    {
        var tracker = new ProgressTracker("EP01", 1_000_000);
        var observed = new List<long>();
        var progress = new SyncProgress<DownloadProgress>(p => observed.Add(p.BytesCompleted));

        tracker.Deliver(progress, At(200));
        tracker.Deliver(progress, At(100));

        Assert.Equal(new[] { 200L }, observed);
    }

    [Fact]
    public void Keeps_delivering_after_a_handler_throws()
    {
        var tracker = new ProgressTracker("EP01", 1_000_000);
        var observed = new List<long>();

        var progress = new SyncProgress<DownloadProgress>(p =>
        {
            if (p.BytesCompleted == 100) throw new InvalidOperationException("bound to a dead view");
            observed.Add(p.BytesCompleted);
        });

        tracker.Deliver(progress, At(100));
        tracker.Deliver(progress, At(200));

        // Swallowing the fault is not enough: the tracker is unusable afterwards if the throw
        // leaves it marked as still reporting.
        Assert.Equal(new[] { 200L }, observed);
    }

    [Fact]
    public void Counts_bytes_from_an_already_completed_resume()
    {
        var tracker = new ProgressTracker("EP01", 1_000, alreadyCompletedBytes: 400);

        tracker.Add(100);

        Assert.Equal(500, tracker.BytesCompleted);
        Assert.Equal(500, tracker.Snapshot().BytesCompleted);
    }
}
