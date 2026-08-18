using LamSims.App.Services;
using LamSims.Core.Downloading;

namespace LamSims.App.Tests;

public class PackQueueControllerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lamsims-controller").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private (PackQueueController Controller, ScriptedRunner Runner, PackQueue Queue) Build()
    {
        var runner = new ScriptedRunner();
        var paths = new DownloadPaths(Path.Combine(_root, "downloads"));
        paths.EnsureCreated();
        var queue = new PackQueue(runner, paths, new QueueOptions { ProgressInterval = TimeSpan.Zero });

        return (new PackQueueController(queue), runner, queue);
    }

    [Fact(Timeout = 15000)]
    public async Task Remove_reports_the_queues_refusal_of_the_active_item()
    {
        // TakeNext registers the active item before the runner reports any phase, so a row
        // reads Queued while it is in flight. The seam must pass core's false through rather
        // than pretending the removal happened.
        var (controller, runner, queue) = Build();
        var run = controller.RunAsync(CancellationToken.None);

        controller.Enqueue(Packs.Entry(), _root);
        await runner.Entered.Task;

        Assert.False(controller.Remove("EP01"));

        runner.Release();
        controller.CancelAll();
        await controller.DisposeAsync();
        await run;
        GC.KeepAlive(queue);
    }

    [Fact(Timeout = 15000)]
    public async Task Remove_takes_a_pending_item_out_of_the_queue()
    {
        var (controller, runner, queue) = Build();
        var run = controller.RunAsync(CancellationToken.None);

        controller.Enqueue(Packs.Entry(), _root);
        await runner.Entered.Task;
        controller.Enqueue(Packs.Entry("EP02", "Get Together"), _root);

        Assert.True(controller.Remove("EP02"));

        runner.Release();
        controller.CancelAll();
        await controller.DisposeAsync();
        await run;
        GC.KeepAlive(queue);
    }

    [Fact(Timeout = 15000)]
    public async Task Cancel_reaches_the_named_pack()
    {
        var (controller, runner, queue) = Build();
        var run = controller.RunAsync(CancellationToken.None);

        controller.Enqueue(Packs.Entry(), _root);
        await runner.Entered.Task;
        controller.Enqueue(Packs.Entry("EP02", "Get Together"), _root);
        controller.Cancel("EP02");

        var seen = await FirstUpdateWhere(controller, u =>
            u.Items.Any(i => i.Code == "EP02" && i.State == QueueItemState.Cancelled));

        Assert.Contains(seen.Items, i => i.Code == "EP02" && i.State == QueueItemState.Cancelled);

        runner.Release();
        controller.CancelAll();
        await controller.DisposeAsync();
        await run;
        GC.KeepAlive(queue);
    }

    private static async Task<QueueUpdate> FirstUpdateWhere(
        IQueueController controller, Func<QueueUpdate, bool> predicate)
    {
        await foreach (var update in controller.Updates)
        {
            if (predicate(update)) return update;
        }

        throw new InvalidOperationException("the queue closed before the expected update arrived");
    }
}
