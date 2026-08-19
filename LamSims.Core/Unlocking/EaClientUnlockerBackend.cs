using LamSims.Core.Downloading;
using LamSims.Core.Settings;

namespace LamSims.Core.Unlocking;

/// <summary>
/// Upstream's EA DLC Unlocker, ported.
///
/// Carries NO platform attribute on purpose: every operating-system facility is behind
/// <see cref="IUnlockerHost"/> and both special-folder roots are injected, so this class touches
/// only the filesystem and the seam and runs under a temp directory on any platform. The Windows
/// surface is <see cref="WindowsUnlockerHost"/> alone.
/// </summary>
// CS9113 fires on a primary-constructor parameter this class does not yet read, and
// TreatWarningsAsErrors makes that a build error. All four are read by the install and remove
// sequences below; the suppression is scoped to the declaration alone rather than the file, and
// exists only because those sequences and this declaration cannot land in the same edit.
#pragma warning disable CS9113
public sealed partial class EaClientUnlockerBackend(
    IUnlockerHost host,
    UnlockerPaths paths,
    AppPaths appPaths,
    IDelayProvider delays) : IUnlockerBackend
#pragma warning restore CS9113
{
    internal const string DllName = "version.dll";
    internal const string ScheduledTaskName = "copy_dlc_unlocker";
    internal const string AutostartValueName = "EADM";
    internal const string MachineIniFlag = "machine.bgsstandaloneenabled=0";

    public string Id => "windows-native";

    public bool IsSupported => host.IsAvailable;

    /// <summary>
    /// Exact names rather than a case-insensitive prefix. Matching anything starting with "EA"
    /// reaches unrelated software such as EarTrumpet. This table lives here rather than in the
    /// host so a test can assert it; the updater's own process name must never appear in it.
    /// </summary>
    internal static readonly Dictionary<ClientKind, string[]> ProcessNames = new()
    {
        [ClientKind.EaApp] =
            ["EADesktop", "EABackgroundService", "EACefSubProcess", "EAConnect_microsoft", "EALocalHostSvc"],
        [ClientKind.Origin] = ["Origin", "OriginWebHelperService", "OriginClientService"],
    };

    private static readonly (ClientRegistryKey Key, ClientKind Kind, string Display)[] Candidates =
    [
        // EA app first, then Origin's 64-bit view, then its 32-bit one.
        (ClientRegistryKey.EaDesktop, ClientKind.EaApp, "EA app"),
        (ClientRegistryKey.OriginWow6432, ClientKind.Origin, "Origin"),
        (ClientRegistryKey.Origin, ClientKind.Origin, "Origin"),
    ];

    public Task<IReadOnlyList<UnlockerTarget>> DetectTargetsAsync(CancellationToken ct)
    {
        if (!host.IsAvailable) return Task.FromResult<IReadOnlyList<UnlockerTarget>>([]);

        foreach (var (key, kind, display) in Candidates)
        {
            var value = host.ReadClientPath(key);
            if (string.IsNullOrEmpty(value)) continue;

            var directory = ClientDirectoryOf(value);
            if (directory is null) continue;

            return Task.FromResult<IReadOnlyList<UnlockerTarget>>(
                [new UnlockerTarget(Id, kind, directory, display)]);
        }

        return Task.FromResult<IReadOnlyList<UnlockerTarget>>([]);
    }

    /// <summary>
    /// The registry value names the client executable, so the target is its containing directory.
    /// A value that is already a directory is accepted as-is.
    /// </summary>
    private static string? ClientDirectoryOf(string value)
    {
        if (Directory.Exists(value)) return Path.TrimEndingDirectorySeparator(value);

        var directory = Path.GetDirectoryName(value);
        return !string.IsNullOrEmpty(directory) && Directory.Exists(directory) ? directory : null;
    }

    public Task<UnlockerStatus> GetStatusAsync(UnlockerTarget target, CancellationToken ct)
    {
        // Presence of the DLL is the whole test. There is no Outdated state and no version,
        // because the unlocker exposes nothing to detect a version from.
        var dll = Path.Combine(target.ClientPath, DllName);

        if (!Directory.Exists(target.ClientPath))
            return Task.FromResult(new UnlockerStatus(UnlockerState.Unknown,
                $"'{target.ClientPath}' cannot be read."));

        return Task.FromResult(File.Exists(dll)
            ? new UnlockerStatus(UnlockerState.Installed, dll)
            : new UnlockerStatus(UnlockerState.NotInstalled, null));
    }

    public Task<UnlockerResult> InstallAsync(UnlockerTarget target, IUnlockerAssetSource assets,
                                             IProgress<UnlockerProgress> progress, CancellationToken ct)
        => throw new NotImplementedException();

    public Task<UnlockerResult> RemoveAsync(UnlockerTarget target,
                                            IProgress<UnlockerProgress> progress, CancellationToken ct)
        => throw new NotImplementedException();
}
