namespace LamSims.Core.Unlocking;

public enum ClientKind { EaApp, Origin }

public enum UnlockerState { NotInstalled, Installed, Unknown }

/// <summary>
/// One place the unlocker can be installed. A list because a backend may find several, one per
/// Wine prefix for instance; the EA-client backend returns zero or one.
/// </summary>
public sealed record UnlockerTarget(string BackendId, ClientKind Client, string ClientPath,
                                   string DisplayName, string? PrefixPath = null);

/// <summary>
/// Carries no version and no Outdated state, because the unlocker exposes nothing to detect a
/// version from. Installed means version.dll is present.
/// </summary>
public sealed record UnlockerStatus(UnlockerState State, string? Detail);

/// <param name="Step">The step about to run, for display.</param>
/// <param name="Completed">Steps already finished. Zero on the first report.</param>
/// <param name="Total">Steps this run will attempt, fixed per ClientKind and operation.</param>
public sealed record UnlockerProgress(string Step, int Completed, int Total);

/// <summary>
/// Warnings sit beside a success because several steps are non-critical; collapsing those into
/// failure would refuse installs that in fact worked. PackQueue carries Warnings the same way.
/// </summary>
public sealed record UnlockerResult(bool Success, string? Error = null,
                                   bool RequiresElevation = false,
                                   IReadOnlyList<string>? Warnings = null)
{
    public static UnlockerResult Ok(IReadOnlyList<string>? warnings = null) => new(true, Warnings: warnings);
    public static UnlockerResult Fail(string error, IReadOnlyList<string>? warnings = null) =>
        new(false, error, Warnings: warnings);
    public static UnlockerResult NeedsElevation() =>
        new(false, "Administrator rights are required.", RequiresElevation: true);
}

public interface IUnlockerBackend
{
    string Id { get; }
    bool IsSupported { get; }

    Task<IReadOnlyList<UnlockerTarget>> DetectTargetsAsync(CancellationToken ct);
    Task<UnlockerStatus> GetStatusAsync(UnlockerTarget target, CancellationToken ct);
    Task<UnlockerResult> InstallAsync(UnlockerTarget target, IUnlockerAssetSource assets,
                                      IProgress<UnlockerProgress> progress, CancellationToken ct);
    Task<UnlockerResult> RemoveAsync(UnlockerTarget target,
                                     IProgress<UnlockerProgress> progress, CancellationToken ct);
}

public interface IUnlockerAssetSource
{
    Task<string> GetDllAsync(ClientKind client, CancellationToken ct);
}

public sealed class UnlockerAssetMismatchException(string url, string expected, string actual)
    : Exception($"The DLL at '{url}' does not match the digest this build pins. " +
                $"Expected {expected}, got {actual}. Upstream may have replaced the release asset.")
{
    public string Url { get; } = url;
    public string Expected { get; } = expected;
    public string Actual { get; } = actual;
}
