using LamSims.Core.Unlocking;

namespace LamSims.App.Tests;

/// <summary>
/// An App-side IUnlockerHost. LamSims.Core.Tests is not referenced from this project, so its
/// FakeUnlockerHost is unreachable; FakeDelayProvider and TestFileServer are duplicated per project
/// for the same reason.
/// </summary>
/// <param name="calls">
/// An externally owned log, mirroring RecordingQueue's own constructor, so a relaunch test can
/// interleave this host's "Relaunch" with the queue's "CancelAll"/"Dispose" in one ordered list;
/// shutdown's ordering rule has no other observable.
/// </param>
public sealed class FakeUnlockerHost(List<string>? calls = null) : IUnlockerHost
{
    public bool IsAvailable { get; set; } = true;
    public bool IsElevated { get; set; } = true;
    public bool RelaunchAccepted { get; set; } = true;
    public List<string> Calls { get; } = calls ?? [];

    public bool TryRelaunchElevated()
    {
        Calls.Add("Relaunch");
        return RelaunchAccepted;
    }

    public string? ReadClientPath(ClientRegistryKey key) => null;
    public string? ReadAutostartValue(string name) => null;
    public void WriteAutostartValue(string name, string value) { }
    public void RemoveAutostartValue(string name) { }
    public IReadOnlyList<string> RunningClientProcesses(IReadOnlyList<string> processNames) => [];
    public IReadOnlyList<string> KillClientProcesses(IReadOnlyList<string> processNames,
                                                   TimeSpan perProcessTimeout) => [];
    public void DeleteScheduledTask(string name) => Calls.Add($"DeleteTask:{name}");
}

/// <summary>
/// Never called: the test hosts register no backend, so nothing can ask for a DLL. The four bytes
/// are an MZ header, so anything that did reach it receives something shaped like a PE image rather
/// than an empty buffer that a length check would silently accept.
/// </summary>
public sealed class StubUnlockerAssets : IUnlockerAssetSource
{
    public Task<string> GetDllAsync(ClientKind client, CancellationToken ct) =>
        throw new InvalidOperationException("No unlocker asset should be fetched in these tests.");
}

/// <summary>
/// Drives a real <see cref="UnlockerService"/> rather than faking the service itself, so the
/// service's id routing stays exercised by these tests.
/// </summary>
public sealed class RecordingUnlockerBackend(params UnlockerTarget[] targets) : IUnlockerBackend
{
    public string Id => "test-backend";
    public bool IsSupported { get; set; } = true;
    public List<string> Calls { get; } = [];
    public UnlockerResult NextResult { get; set; } = UnlockerResult.Ok();
    public UnlockerState State { get; set; } = UnlockerState.NotInstalled;
    public List<UnlockerProgress> ToReport { get; } = [];

    /// <summary>The managed thread InstallAsync/RemoveAsync ran on, so a test can assert the
    /// operation left the caller's thread.</summary>
    public int OperationThreadId { get; private set; }

    /// <summary>
    /// Set to keep an operation in flight, the way a slow filesystem step would. Without it a test
    /// about IsBusy DURING the operation can never observe it: the fake would return before anyone
    /// looked, the same reason StubQueue.HoldDispose exists.
    /// </summary>
    public TaskCompletionSource? Hold { get; set; }

    public Task<IReadOnlyList<UnlockerTarget>> DetectTargetsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<UnlockerTarget>>(targets);

    public Task<UnlockerStatus> GetStatusAsync(UnlockerTarget t, CancellationToken ct) =>
        Task.FromResult(new UnlockerStatus(State, null));

    public async Task<UnlockerResult> InstallAsync(UnlockerTarget t, IUnlockerAssetSource a,
                                           IProgress<UnlockerProgress> p, CancellationToken ct)
    {
        OperationThreadId = Environment.CurrentManagedThreadId;

        // The target's path, not just the verb: a row bound to its neighbour's data context calls the
        // right method for the wrong client, and only the path reveals it.
        Calls.Add($"Install:{t.ClientPath}");
        foreach (var report in ToReport) p.Report(report);

        if (Hold is not null) await Hold.Task;

        return NextResult;
    }

    public async Task<UnlockerResult> RemoveAsync(UnlockerTarget t, IProgress<UnlockerProgress> p,
                                          CancellationToken ct)
    {
        OperationThreadId = Environment.CurrentManagedThreadId;

        Calls.Add($"Remove:{t.ClientPath}");

        if (Hold is not null) await Hold.Task;

        return NextResult;
    }
}
