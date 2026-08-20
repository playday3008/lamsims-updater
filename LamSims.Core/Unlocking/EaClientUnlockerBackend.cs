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
public sealed partial class EaClientUnlockerBackend(
    IUnlockerHost host,
    UnlockerPaths paths,
    AppPaths appPaths,
    IDelayProvider delays) : IUnlockerBackend
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

    internal const int InstallStepsEaApp = 10;
    internal const int InstallStepsOrigin = 8;

    private static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Issues one report per step, naming the step about to run and counting those finished, plus a
    /// final report at Total/Total. Without that trailing report a completed install would display
    /// Total-1/Total and the bar would never fill.
    /// </summary>
    private sealed class Steps(IProgress<UnlockerProgress> progress, int total)
    {
        private int _done;

        public void Begin(string step) => progress.Report(new UnlockerProgress(step, _done, total));
        public void Done() => _done++;
        public void Finish(string step) => progress.Report(new UnlockerProgress(step, _done, total));
        public int Completed => _done;
    }

    public async Task<UnlockerResult> InstallAsync(UnlockerTarget target, IUnlockerAssetSource assets,
                                                  IProgress<UnlockerProgress> progress,
                                                  CancellationToken ct)
    {
        var ea = target.Client == ClientKind.EaApp;
        var steps = new Steps(progress, ea ? InstallStepsEaApp : InstallStepsOrigin);
        var warnings = new List<string>();

        // Step 1. Checked before anything is mutated: without it the config files and the
        // autostart change land and only the DLL copy into Program Files fails, leaving a
        // half-installed unlocker.
        steps.Begin("Checking administrator rights");
        if (!host.IsElevated) return UnlockerResult.NeedsElevation();
        steps.Done();

        // Step 2. Before any mutation, so a digest mismatch or a dead mirror changes nothing.
        steps.Begin("Fetching the unlocker");
        string dll;
        try
        {
            dll = await assets.GetDllAsync(target.Client, ct);
        }
        // UnauthorizedAccessException does not derive from IOException, and the asset source writes
        // into a cache directory the user can point anywhere, so a refused cache is an ordinary
        // failure of this step rather than a fault that should escape the operation.
        catch (Exception e) when (e is UnlockerAssetMismatchException or HttpRequestException
                                       or IOException or SizeMismatchException
                                       or UnauthorizedAccessException)
        {
            return UnlockerResult.Fail(e.Message);
        }
        steps.Done();

        // Step 3. A client that outlives its kill timeout still holds a lock on the directory
        // step 7 writes into, so the survivors are reported here rather than surfacing there.
        steps.Begin("Stopping the client");
        var names = ProcessNames[target.Client];
        if (host.RunningClientProcesses(names).Count > 0)
        {
            var survivors = host.KillClientProcesses(names, KillTimeout);
            if (survivors.Count > 0)
                return UnlockerResult.Fail(
                    $"These processes are still running and hold the client directory open: " +
                    $"{string.Join(", ", survivors)}. Close them and try again.");

            await delays.DelayAsync(SettleDelay, ct);
        }
        steps.Done();

        // Step 4. Its own report, not folded into step 5: every step must Begin exactly once, or
        // Completed skips a number and the run emits one report fewer than Total + 1.
        steps.Begin("Creating the configuration folder");
        try
        {
            Directory.CreateDirectory(paths.ConfigDirectory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return UnlockerResult.Fail(
                $"'{paths.ConfigDirectory}' could not be created: {e.Message}");
        }
        steps.Done();

        // Step 5. WriteAsync creates the directory too, which is idempotent and harmless.
        steps.Begin("Writing the unlocker configuration");
        try
        {
            await UnlockerConfigWriter.WriteAsync(paths, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return UnlockerResult.Fail($"The unlocker configuration could not be written: {e.Message}");
        }
        steps.Done();

        return await InstallRestAsync(target, dll, steps, warnings, ea, ct);
    }

    private async Task<UnlockerResult> InstallRestAsync(UnlockerTarget target, string dll, Steps steps,
                                                       List<string> warnings, bool ea,
                                                       CancellationToken ct)
    {
        // Step 6, non-fatal: nothing to remove is the normal case.
        steps.Begin("Removing older unlocker files");
        foreach (var name in new[] { "version_o.dll", "winhttp.dll", "winhttp_o.dll" })
            TryDelete(Path.Combine(target.ClientPath, name), warnings);
        try
        {
            foreach (var stale in Directory.GetFiles(target.ClientPath, "w_*.ini"))
                TryDelete(stale, warnings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Older unlocker .ini files could not be listed: {e.Message}");
        }
        steps.Done();

        // Step 7, fatal: without the DLL on disk nothing is installed, whatever else succeeded.
        steps.Begin($"Copying {DllName}");
        var installed = Path.Combine(target.ClientPath, DllName);
        try
        {
            File.Copy(dll, installed, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return UnlockerResult.Fail(
                $"{DllName} could not be written to '{target.ClientPath}': {e.Message}", warnings);
        }
        steps.Done();

        if (ea)
        {
            // Step 8, non-fatal. This copy is what survives an EA app self-update; without it the
            // unlocker works now and stops working after the client updates itself. It is written
            // from the verified bytes still in hand rather than re-read from disk.
            steps.Begin("Copying to StagedEADesktop");
            try
            {
                var parent = Directory.GetParent(target.ClientPath)?.FullName
                    ?? throw new IOException($"'{target.ClientPath}' has no parent directory.");
                var staged = Path.Combine(parent, "StagedEADesktop", "EA Desktop");
                Directory.CreateDirectory(staged);
                File.Copy(dll, Path.Combine(staged, DllName), overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"The StagedEADesktop copy failed, so the unlocker will need " +
                             $"reinstalling after EA app updates itself: {e.Message}");
            }
            steps.Done();

            // Step 9, non-fatal: the flag is a convenience and the install stands without it.
            steps.Begin("Updating machine.ini");
            try
            {
                if (!await IniFlagEditor.AddFlagAsync(paths.MachineIniFile, MachineIniFlag, ct)
                    && !File.Exists(paths.MachineIniFile))
                    warnings.Add($"machine.ini was not found at '{paths.MachineIniFile}', so it was " +
                                 "left alone. This is not critical.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"machine.ini could not be updated: {e.Message}");
            }
            steps.Done();
        }

        // Step 10, non-fatal and reversible: the previous value is backed up first so removal can
        // put it back, rather than leaving the user's login behaviour changed.
        steps.Begin("Updating autostart");
        try
        {
            var existing = host.ReadAutostartValue(AutostartValueName);
            if (existing is not null)
            {
                Directory.CreateDirectory(appPaths.Root);
                await AtomicFile.WriteAllTextAsync(appPaths.UnlockerAutostartBackupFile,
                    System.Text.Json.JsonSerializer.Serialize(
                        new AutostartBackup(AutostartValueName, existing)), ct);
                host.RemoveAutostartValue(AutostartValueName);
            }
        }
        // SecurityException is here on purpose: the host guards its registry READS but not its
        // writes, because it holds no policy, and a group-policy-restricted Run key throws exactly
        // this. Without it this non-fatal step would abort the install after the DLL is already in
        // place.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or System.Security.SecurityException)
        {
            warnings.Add($"The autostart entry could not be changed: {e.Message}");
        }
        steps.Done();

        steps.Finish("Done");
        return UnlockerResult.Ok(warnings);
    }

    private static void TryDelete(string path, List<string> warnings)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"'{Path.GetFileName(path)}' could not be removed: {e.Message}");
        }
    }

    internal sealed record AutostartBackup(string Name, string Value);

    internal const int RemoveStepsEaApp = 9;
    internal const int RemoveStepsOrigin = 6;

    public async Task<UnlockerResult> RemoveAsync(UnlockerTarget target,
                                                 IProgress<UnlockerProgress> progress,
                                                 CancellationToken ct)
    {
        var ea = target.Client == ClientKind.EaApp;
        var steps = new Steps(progress, ea ? RemoveStepsEaApp : RemoveStepsOrigin);
        var warnings = new List<string>();

        // Step 1, before the elevation check on purpose: a machine with nothing installed must not
        // raise a UAC prompt to tell the user there is nothing to do. "Nothing installed" is not
        // just an absent DLL, because an EA app self-update can delete it on its own (which is why
        // the StagedEADesktop copy exists) and install also flipped the autostart value and wrote a
        // backup. Any of those three surviving means there is still something to undo, or removal
        // early-returns having restored nothing and the user's autostart stays broken.
        steps.Begin("Checking what is installed");
        var installed = Path.Combine(target.ClientPath, DllName);
        var somethingToRemove = File.Exists(installed)
            || File.Exists(appPaths.UnlockerAutostartBackupFile)
            || Directory.Exists(paths.ConfigDirectory);
        if (!somethingToRemove)
        {
            steps.Done();
            steps.Finish("Nothing to remove");
            return UnlockerResult.Ok();
        }
        steps.Done();

        steps.Begin("Checking administrator rights");
        if (!host.IsElevated) return UnlockerResult.NeedsElevation();
        steps.Done();

        steps.Begin("Stopping the client");
        var names = ProcessNames[target.Client];
        if (host.RunningClientProcesses(names).Count > 0)
        {
            var survivors = host.KillClientProcesses(names, KillTimeout);
            if (survivors.Count > 0)
                return UnlockerResult.Fail(
                    $"These processes are still running and hold the client directory open: " +
                    $"{string.Join(", ", survivors)}. Close them and try again.");

            await delays.DelayAsync(SettleDelay, ct);
        }
        steps.Done();

        if (ea)
        {
            // Step 4. Nothing here ever creates this task, but one left by an older install must
            // still be removed, or every upgrader keeps a stale elevated task forever.
            steps.Begin("Removing the scheduled task");
            try
            {
                host.DeleteScheduledTask(ScheduledTaskName);
            }
            catch (Exception e) when (e is InvalidOperationException or IOException)
            {
                warnings.Add($"The '{ScheduledTaskName}' scheduled task could not be removed: {e.Message}");
            }
            steps.Done();
        }

        // Step 5, fatal: if this fails the unlocker is still loaded and the user has been told it
        // was removed.
        steps.Begin($"Removing {DllName}");
        try
        {
            // Guarded rather than bare: the probe above also proceeds when only the autostart backup
            // survives, and in that case the client directory may be gone entirely. Windows'
            // File.Delete raises DirectoryNotFoundException, an IOException, for a missing
            // DIRECTORY though not for a missing file, which would fail this fatal step and leave
            // the autostart unrestored.
            if (File.Exists(installed)) File.Delete(installed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return UnlockerResult.Fail($"{DllName} could not be removed from " +
                                       $"'{target.ClientPath}': {e.Message}", warnings);
        }
        steps.Done();

        if (ea)
        {
            steps.Begin("Cleaning up StagedEADesktop");
            try
            {
                var parent = Directory.GetParent(target.ClientPath)?.FullName;
                if (parent is not null)
                {
                    var staged = Path.Combine(parent, "StagedEADesktop");
                    var inner = Path.Combine(staged, "EA Desktop");
                    TryDelete(Path.Combine(inner, DllName), warnings);

                    // Only when empty: the folder is EA's, and something else may legitimately
                    // be staged there.
                    foreach (var directory in new[] { inner, staged })
                        if (Directory.Exists(directory) &&
                            !Directory.EnumerateFileSystemEntries(directory).Any())
                            Directory.Delete(directory);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"StagedEADesktop could not be cleaned up: {e.Message}");
            }
            steps.Done();

            steps.Begin("Restoring machine.ini");
            try
            {
                await IniFlagEditor.RemoveFlagAsync(paths.MachineIniFile, MachineIniFlag, ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"machine.ini could not be restored: {e.Message}");
            }
            steps.Done();
        }

        // Step 8: this is what makes the unlocker a reversible change to the machine. It runs
        // before the config directory is deleted, and the backup deliberately lives under AppPaths
        // so that ordering is not load-bearing.
        steps.Begin("Restoring autostart");
        try
        {
            var backupFile = appPaths.UnlockerAutostartBackupFile;
            if (File.Exists(backupFile))
            {
                AutostartBackup? backup;
                try
                {
                    backup = System.Text.Json.JsonSerializer.Deserialize<AutostartBackup>(
                        await File.ReadAllTextAsync(backupFile, ct));
                }
                catch (System.Text.Json.JsonException e)
                {
                    // An unreadable backup cannot restore anything, and leaving it on disk would
                    // keep step 1 finding something to remove for ever, so every later removal
                    // would demand elevation, stop the client and restore nothing.
                    backup = null;
                    warnings.Add($"The autostart backup could not be read, so it was discarded: {e.Message}");
                }

                if (string.IsNullOrEmpty(backup?.Name) || backup.Value is null)
                {
                    if (backup is not null)
                        warnings.Add("The autostart backup was incomplete, so it was discarded.");
                }
                // Only when the slot is still empty. The client can re-create its own Run entry with
                // a newer path between install and removal, and restoring the recorded value over
                // that would downgrade the user's autostart to a stale command line.
                else if (host.ReadAutostartValue(backup.Name) is null)
                {
                    host.WriteAutostartValue(backup.Name, backup.Value);
                }
                else
                {
                    warnings.Add("The autostart entry had been re-created, so it was left as it is.");
                }

                File.Delete(backupFile);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or System.Security.SecurityException)
        {
            warnings.Add($"The autostart entry could not be restored: {e.Message}");
        }
        steps.Done();

        steps.Begin("Removing the unlocker configuration");
        try
        {
            if (Directory.Exists(paths.ConfigDirectory))
                Directory.Delete(paths.ConfigDirectory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"The unlocker configuration folder could not be removed: {e.Message}");
        }
        steps.Done();

        steps.Finish("Done");
        return UnlockerResult.Ok(warnings);
    }
}
