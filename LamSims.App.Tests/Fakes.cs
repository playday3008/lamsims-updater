using System.Threading.Channels;
using LamSims.App.Services;

namespace LamSims.App.Tests;

/// <summary>
/// Records every call in order. The recorded sequence is what the shutdown test asserts.
/// </summary>
public sealed class RecordingQueue : IQueueController
{
    private readonly Channel<QueueUpdate> _updates = Channel.CreateUnbounded<QueueUpdate>();

    public List<string> Calls { get; } = new();

    public List<(string Code, string GameDirectory)> Enqueued { get; } = new();

    public bool RemoveResult { get; set; } = true;

    public IAsyncEnumerable<QueueUpdate> Updates => _updates.Reader.ReadAllAsync();

    public void Publish(QueueUpdate update) => _updates.Writer.TryWrite(update);

    public Task RunAsync(CancellationToken ct)
    {
        Calls.Add("Run");
        return Task.CompletedTask;
    }

    public void Enqueue(PackEntry pack, string gameDirectory)
    {
        Calls.Add($"Enqueue:{pack.Code}");
        Enqueued.Add((pack.Code, gameDirectory));
    }

    public void Cancel(string code) => Calls.Add($"Cancel:{code}");

    public bool Remove(string code)
    {
        Calls.Add($"Remove:{code}");
        return RemoveResult;
    }

    public void CancelAll() => Calls.Add("CancelAll");

    public void Complete() => Calls.Add("Complete");

    public void Pause() => Calls.Add("Pause");

    public void Resume() => Calls.Add("Resume");

    public ValueTask DisposeAsync()
    {
        Calls.Add("Dispose");
        _updates.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

public static class Packs
{
    public static PackEntry Entry(string code = "EP01", string name = "Get to Work", long size = 12_400_000_000) =>
        new(code, name, PackType.Expansion, size, null, new string('a', 64),
            [new Uri("https://example.invalid/" + code + ".zip")],
            [code]);
}
