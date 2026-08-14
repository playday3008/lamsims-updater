using System.Collections.Concurrent;
using LamSims.Core.Downloading;

namespace LamSims.Core.Tests;

public class ChunkQueueTests
{
    [Fact]
    public void Hands_out_each_chunk_once_then_reports_empty()
    {
        var queue = new ChunkQueue(ChunkPlan.Create(300, 100));

        Assert.True(queue.TryTake(out var first));
        Assert.True(queue.TryTake(out _));
        Assert.True(queue.TryTake(out _));
        Assert.False(queue.TryTake(out _));
        Assert.Equal(0, first!.Index);
        Assert.Equal(0, queue.RemainingCount);
    }

    [Fact]
    public void Requeued_chunks_are_handed_out_again()
    {
        var queue = new ChunkQueue(ChunkPlan.Create(100, 100));
        queue.TryTake(out var chunk);

        queue.Requeue(chunk!);

        Assert.Equal(1, queue.RemainingCount);
        Assert.True(queue.TryTake(out var again));
        Assert.Equal(chunk, again);
    }

    [Fact]
    public void Concurrent_workers_never_receive_the_same_chunk_twice()
    {
        var queue = new ChunkQueue(ChunkPlan.Create(10_000, 1));
        var taken = new ConcurrentBag<int>();

        Parallel.For(0, 8, _ =>
        {
            while (queue.TryTake(out var chunk))
                taken.Add(chunk!.Index);
        });

        Assert.Equal(10_000, taken.Count);
        Assert.Equal(10_000, taken.Distinct().Count());
    }
}
