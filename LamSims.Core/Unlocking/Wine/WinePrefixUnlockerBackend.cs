using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LamSims.Core.Downloading;
using LamSims.Core.Settings;

namespace LamSims.Core.Unlocking.Wine;

/// <summary>
/// The Linux backend. It owns no install logic of its own: per prefix it builds a
/// <see cref="WineUnlockerHost"/>, an <see cref="UnlockerPaths"/> over the prefix's RESOLVED
/// Windows roots and an inner <see cref="EaClientUnlockerBackend"/>, and adds two steps at each end
/// — verify the prefix, set or clear the Wine DLL override. Every inner step sequence is unchanged.
///
/// Routing back is stateless: an operation rebuilds the inner backend from
/// <c>target.PrefixPath</c>, so nothing has to survive between detection and install.
/// </summary>
/// <param name="configuredPrefix">
/// Read LIVE, per scan, so changing the setting re-runs detection without rebuilding the graph.
/// </param>
public sealed class WinePrefixUnlockerBackend(
    LauncherHomes homes, AppPaths appPaths, IDelayProvider delays, IWineProcesses processes,
    string userName, Func<string?> configuredPrefix, IProgress<string>? notes = null)
    : IUnlockerBackend
{
    public string Id => "wine-prefix";

    /// <summary>
    /// Not on Windows, where the EA-client backend owns the machine's real registry. Both backends
    /// are registered and each reports for its own platform.
    /// </summary>
    public bool IsSupported => !OperatingSystem.IsWindows();

    private readonly WineOverrideStore _overrides = new(appPaths);

    private const int ExtraInstallSteps = 2;
    private const int ExtraRemoveSteps = 2;

    public async Task<IReadOnlyList<UnlockerTarget>> DetectTargetsAsync(CancellationToken ct)
    {
        if (!IsSupported) return [];

        // A record outlives its prefix, and the undo would then write a recorded value into a
        // registry that was regenerated in the meantime. Detection is the only thing that runs
        // often enough to catch it.
        foreach (var orphan in WineDllOverride.SweepOrphans(_overrides, userName))
            notes?.Report(orphan);

        var found = new List<UnlockerTarget>();

        foreach (var prefix in Scanner().Scan(configuredPrefix()))
        {
            var host = new WineUnlockerHost(prefix, notes);
            var inner = Inner(prefix, host);
            if (inner is null) continue;

            var targets = await inner.DetectTargetsAsync(ct);

            if (targets.Count == 0)
            {
                // Only when no candidate key held a value at all. When one did, the host has
                // already reported the more specific "registered but its path does not exist".
                if (!host.SawClientValue)
                {
                    notes?.Report($"{prefix.Environment.Describe()}: no EA app or Origin is "
                                  + $"registered in '{prefix.Root}'.");
                }

                continue;
            }

            foreach (var target in targets)
            {
                // Per TARGET, and in exactly one place. Origin is 32-bit and works in either arch,
                // so a win32 prefix with Origin registered must still yield a target; rejecting the
                // prefix would hide it.
                if (prefix.Arch == WineArch.Win32 && target.Client == ClientKind.EaApp)
                {
                    notes?.Report($"'{prefix.Root}' is 32-bit and the EA app needs a 64-bit prefix.");
                    continue;
                }

                // DisplayName is left alone: the row's Title composes it with Environment, and
                // rewriting it here makes that rule unreachable.
                found.Add(target with
                {
                    BackendId = Id,
                    PrefixPath = prefix.Root,
                    Environment = prefix.Environment,
                });
            }
        }

        return found;
    }

    public Task<UnlockerStatus> GetStatusAsync(UnlockerTarget target, CancellationToken ct)
    {
        var opened = Reopen(target);

        return opened is null
            ? Task.FromResult(new UnlockerStatus(UnlockerState.Unknown,
                                                 $"'{target.PrefixPath}' no longer exists."))
            : opened.Value.Inner.GetStatusAsync(target, ct);
    }

    public async Task<UnlockerResult> InstallAsync(UnlockerTarget target,
                                                  IUnlockerAssetSource assets,
                                                  IProgress<UnlockerProgress> progress,
                                                  CancellationToken ct)
    {
        var ea = target.Client == ClientKind.EaApp;
        var innerTotal = ea ? EaClientUnlockerBackend.InstallStepsEaApp
                            : EaClientUnlockerBackend.InstallStepsOrigin;
        var total = innerTotal + ExtraInstallSteps;

        // Step 1.
        progress.Report(new UnlockerProgress("Checking the Wine prefix", 0, total));

        var opened = Reopen(target);
        if (opened is null) return UnlockerResult.Fail($"'{target.PrefixPath}' no longer exists.");

        var (prefix, _, inner) = opened.Value;
        var finding = new LauncherOverrides(homes).For(prefix.Root);
        var writeNeeded = NeedsWrite(prefix, finding.Verdict);

        // Liveness gates the WRITE, not the operation. A running client implies a live wineserver,
        // so gating the operation would make the restart warning below unreachable and turn every
        // ordinary reinstall into a refusal.
        if (writeNeeded && processes.IsPrefixLive(prefix.Root))
        {
            return UnlockerResult.Fail(
                "Something is still using this Wine prefix, and the Wine DLL override has to be "
                + $"written while it is idle. Close {ClientSubject(target)}, the game, and any "
                + "launcher window using it, then try again.");
        }

        var result = await inner.InstallAsync(
            target, assets, new Wrapped(progress, innerTotal, total, offset: 1), ct);
        if (!result.Success) return result;

        var warnings = new List<string>(result.Warnings ?? []);
        warnings.AddRange(finding.Warnings);
        warnings.AddRange(finding.Unread);

        if (LauncherOverrides.AppDefaultsConflict(prefix, ClientExe(target)) is { } conflict)
            warnings.Add(conflict);

        // Step 12: mirror the configuration where the prefix has more than one Windows user, then
        // set the override. Re-checked for liveness because the fetch above takes seconds.
        progress.Report(new UnlockerProgress("Enabling the DLL override", total - 1, total));

        warnings.AddRange(Mirror(prefix));

        if (writeNeeded && processes.IsPrefixLive(prefix.Root))
        {
            return UnlockerResult.Fail(
                "The DLL was installed, but the Wine DLL override could not be set because "
                + "something started using this prefix. Close everything using it and run the "
                + "install again.", warnings);
        }

        if (await WineDllOverride.ApplyAsync(prefix, _overrides, finding.Verdict, ct) is { } error)
            return UnlockerResult.Fail(error, warnings);

        // A running client keeps the old DLL mapped until it restarts, and nothing here is ever
        // killed: killing wineserver can lose registry state and a shared prefix takes a running
        // game down with it. So this is a warning, not a failure.
        var running = processes.RunningClients(prefix.Root,
                                               EaClientUnlockerBackend.ProcessNames[target.Client]);
        if (running.Count > 0)
            warnings.Add($"{target.DisplayName} is running. Restart it for the unlocker to take effect.");

        progress.Report(new UnlockerProgress("Done", total, total));

        return UnlockerResult.Ok(warnings);
    }

    public async Task<UnlockerResult> RemoveAsync(UnlockerTarget target,
                                                 IProgress<UnlockerProgress> progress,
                                                 CancellationToken ct)
    {
        var ea = target.Client == ClientKind.EaApp;
        var innerTotal = ea ? EaClientUnlockerBackend.RemoveStepsEaApp
                            : EaClientUnlockerBackend.RemoveStepsOrigin;
        var total = innerTotal + ExtraRemoveSteps;

        // Step 1, and BEFORE anything is mutated. Putting this check last would delete version.dll
        // and only then discover the override could not be cleared, leaving the DLL gone, the
        // override set and the record on disk.
        progress.Report(new UnlockerProgress("Checking the Wine prefix", 0, total));

        var opened = Reopen(target);
        if (opened is null) return UnlockerResult.Fail($"'{target.PrefixPath}' no longer exists.");

        var (prefix, _, inner) = opened.Value;
        var undoNeeded = _overrides.Read(prefix.Root)?.WroteRegistry == true;

        if (undoNeeded && processes.IsPrefixLive(prefix.Root))
        {
            return UnlockerResult.Fail(
                "Something is still using this Wine prefix, and the Wine DLL override has to be "
                + "cleared while it is idle. Close everything using it and try again. Nothing has "
                + "been removed.");
        }

        var result = await inner.RemoveAsync(
            target, new Wrapped(progress, innerTotal, total, offset: 1), ct);
        if (!result.Success) return result;

        var warnings = new List<string>(result.Warnings ?? []);

        // Step 11, and it runs whatever the engine returned: the engine's own probe can early-return
        // "nothing to remove" while an override of ours survives.
        progress.Report(new UnlockerProgress("Clearing the DLL override", total - 1, total));

        warnings.AddRange(Unmirror(prefix));

        // One "*version" entry serves every client in the prefix, exactly as one configuration
        // directory does, so it is only cleared when nobody else is left. The record stays too, so
        // the sibling's own removal is the one that undoes it.
        if (await SiblingInstalled(inner, target, ct))
        {
            warnings.Add("Another client in this Wine prefix still uses the DLL override, so it "
                         + "was left in place.");
        }
        else if (await WineDllOverride.UndoAsync(prefix, _overrides, ct) is { } error)
        {
            return UnlockerResult.Fail(error, warnings);
        }

        progress.Report(new UnlockerProgress("Done", total, total));

        return UnlockerResult.Ok(warnings);
    }

    private WinePrefixScanner Scanner() => new(homes, userName, notes);

    private static string ClientExe(UnlockerTarget target) =>
        target.Client == ClientKind.EaApp ? "EADesktop.exe" : "Origin.exe";

    /// <summary>
    /// The client, as named in a sentence telling the user to close it. "EA app" reads as a common
    /// noun and needs the article; "Origin" is a proper noun and does not, so this is not a bare
    /// DisplayName interpolation — it is what makes the Origin refusal say "Close Origin" rather
    /// than sending an Origin user to close a client that has nothing to do with what they are
    /// unlocking.
    /// </summary>
    private static string ClientSubject(UnlockerTarget target) =>
        target.Client == ClientKind.EaApp ? "the EA app" : target.DisplayName;

    /// <summary>
    /// Exactly the condition under which <see cref="WineDllOverride.ApplyAsync"/> writes, because
    /// this is what gates the liveness refusals that protect the write. Any term here that Apply
    /// does not have describes a write that happens with no gate in front of it — a repair, where
    /// a record exists but the entry it describes has gone, would then read-modify-replace the
    /// whole of user.reg while wineserver was free to flush over it.
    /// </summary>
    private static bool NeedsWrite(WinePrefix prefix, OverrideVerdict verdict) =>
        verdict != OverrideVerdict.SuppliesNative && !WineDllOverride.IsSatisfied(prefix);

    private (WinePrefix Prefix, WineUnlockerHost Host, EaClientUnlockerBackend Inner)? Reopen(
        UnlockerTarget target)
    {
        if (target.PrefixPath is null) return null;

        var environment = target.Environment
                          ?? new TargetEnvironment(EnvironmentSource.Wine, target.PrefixPath);
        var prefix = WinePrefix.TryOpen(target.PrefixPath, environment, userName, notes);
        if (prefix is null) return null;

        var host = new WineUnlockerHost(prefix, notes);
        var inner = Inner(prefix, host);

        return inner is null ? null : (prefix, host, inner);
    }

    private EaClientUnlockerBackend? Inner(WinePrefix prefix, WineUnlockerHost host)
    {
        var paths = prefix.PathsFor(prefix.PrimaryUserDirectory);

        if (paths is null)
        {
            notes?.Report($"'{prefix.Root}' has no reachable Windows user directory, so the "
                          + "unlocker configuration has nowhere to go.");
            return null;
        }

        return new EaClientUnlockerBackend(host, paths, appPaths, delays);
    }

    /// <summary>
    /// Copies the configuration the engine wrote into every OTHER Windows user directory. The engine
    /// takes one UnlockerPaths and its step count is fixed, so this is how the configuration reaches
    /// every user without running the engine more than once. Idempotent.
    /// </summary>
    private static IEnumerable<string> Mirror(WinePrefix prefix)
    {
        if (prefix.WindowsUserDirectories.Count < 2) yield break;

        var source = prefix.PathsFor(prefix.PrimaryUserDirectory)?.ConfigDirectory;
        if (source is null || !Directory.Exists(source)) yield break;

        foreach (var user in prefix.WindowsUserDirectories.Skip(1))
        {
            var destination = prefix.PathsFor(user)?.ConfigDirectory;
            if (destination is null) continue;

            // The message is captured rather than yielded directly: a yield return cannot appear
            // inside a catch clause (CS1631), so the failure is built here and returned once the
            // catch has exited.
            string? failure = null;
            try
            {
                Directory.CreateDirectory(destination);
                foreach (var file in Directory.GetFiles(source))
                    File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failure = $"The unlocker configuration could not be copied to "
                          + $"'{destination}': {e.Message}";
            }

            if (failure is not null) yield return failure;
        }
    }

    /// <summary>
    /// Deletes the mirrored configuration directories, and ONLY when the engine deleted the primary
    /// one. The engine's own conditional delete is the decision; this follows it.
    /// </summary>
    private static IEnumerable<string> Unmirror(WinePrefix prefix)
    {
        if (prefix.WindowsUserDirectories.Count < 2) yield break;

        var primary = prefix.PathsFor(prefix.PrimaryUserDirectory)?.ConfigDirectory;
        if (primary is null || Directory.Exists(primary)) yield break;

        foreach (var user in prefix.WindowsUserDirectories.Skip(1))
        {
            var mirrored = prefix.PathsFor(user)?.ConfigDirectory;
            if (mirrored is null || !Directory.Exists(mirrored)) continue;

            // Same CS1631 workaround as Mirror: capture inside the catch, yield after it.
            string? failure = null;
            try
            {
                Directory.Delete(mirrored, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failure = $"'{mirrored}' could not be removed: {e.Message}";
            }

            if (failure is not null) yield return failure;
        }
    }

    /// <summary>
    /// Whether another client directory in this prefix still holds the unlocker DLL. By PATH, not by
    /// kind: two same-kind clients at different directories defeat a kind-keyed check.
    /// </summary>
    private static async Task<bool> SiblingInstalled(EaClientUnlockerBackend inner,
                                                     UnlockerTarget target, CancellationToken ct)
    {
        var self = PathIdentity.Canonical(target.ClientPath) ?? target.ClientPath;

        return (await inner.DetectTargetsAsync(ct)).Any(
            other => !string.Equals(PathIdentity.Canonical(other.ClientPath) ?? other.ClientPath,
                                    self, StringComparison.OrdinalIgnoreCase)
                     && File.Exists(Path.Combine(other.ClientPath,
                                                 EaClientUnlockerBackend.DllName)));
    }

    /// <summary>
    /// Offsets the inner engine's reports and drops its trailing Total/Total one. The operation is
    /// not done when the engine is, and passing that report through would put two reports at the
    /// same count and leave the run one short of Total + 1. Begin never reports at the inner total —
    /// the last Begin is at total - 1 — so the comparison is unambiguous.
    /// </summary>
    /// <remarks>
    /// On the engine's early "Nothing to remove" path the inner run reports only twice and well
    /// below its total, so the wrapped sequence has a gap before this type's own final two reports.
    /// That is deliberate: the alternative is inventing reports for steps that did not run. The
    /// contiguity contract applies to a run that reaches the end, which is what the progress tests
    /// drive.
    /// </remarks>
    private sealed class Wrapped(IProgress<UnlockerProgress> outer, int innerTotal, int total,
                                int offset) : IProgress<UnlockerProgress>
    {
        public void Report(UnlockerProgress value)
        {
            if (value.Completed >= innerTotal) return;

            outer.Report(new UnlockerProgress(value.Step, value.Completed + offset, total));
        }
    }
}
