using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LamSims.Core.Downloading;
using LamSims.Core.Logging;
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
    IDelayProvider delays,
    UnlockerInstallRecordStore? records = null,
    ILogSink? log = null) : IUnlockerBackend
{
    private readonly UnlockerInstallRecordStore _records =
        records ?? new UnlockerInstallRecordStore(appPaths);

    private readonly ILogSink _log = log ?? NullLogSink.Instance;

    /// <summary>
    /// What "one install of the unlocker" means. The configuration directory is shared by every
    /// client that reads it, so it is the natural identity: one value on Windows, one per Wine
    /// prefix elsewhere.
    /// </summary>
    private string Scope => paths.ConfigDirectory;

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
        // The EA app's two registry views first, then Origin's. Order decides which of two views
        // naming one directory survives deduplication, so the EA app's non-Wow6432 view comes
        // first. Origin's pair is the other way round, kept exactly as upstream had it.
        (ClientRegistryKey.EaDesktop, ClientKind.EaApp, "EA app"),
        (ClientRegistryKey.EaDesktopWow6432, ClientKind.EaApp, "EA app"),
        (ClientRegistryKey.OriginWow6432, ClientKind.Origin, "Origin"),
        (ClientRegistryKey.Origin, ClientKind.Origin, "Origin"),
    ];

    public Task<IReadOnlyList<UnlockerTarget>> DetectTargetsAsync(CancellationToken ct)
    {
        if (!host.IsAvailable) return Task.FromResult<IReadOnlyList<UnlockerTarget>>([]);

        var found = new List<UnlockerTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, kind, display) in Candidates)
        {
            var value = host.ReadClientPath(key);
            if (string.IsNullOrEmpty(value)) continue;

            var directory = ClientDirectoryOf(value);
            if (directory is null) continue;

            // Two registry views routinely name one directory, and the first-hit return this
            // replaced collapsed them by accident. Canonical form under OrdinalIgnoreCase, the
            // rule PathIdentity documents for every catalog-to-filesystem comparison; a
            // ToLowerInvariant plus Ordinal pair disagrees with it on some characters.
            var identity = PathIdentity.Canonical(directory);
            if (identity is null || !seen.Add(identity)) continue;

            var target = new UnlockerTarget(Id, kind, directory, display);
            _log.Write(LogLine.Info($"Detected {target.DisplayName} at {target.ClientPath}"));
            found.Add(target);
        }

        return Task.FromResult<IReadOnlyList<UnlockerTarget>>(found);
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
    private sealed class Steps(IProgress<UnlockerProgress> progress, int total, ILogSink log, string? code)
    {
        private int _done;

        public void Begin(string step)
        {
            progress.Report(new UnlockerProgress(step, _done, total));
            log.Write(LogLine.Info($"Step {_done + 1}/{total}: {step}", code));
        }

        public void Done() => _done++;
        public void Finish(string step) => progress.Report(new UnlockerProgress(step, _done, total));
        public int Completed => _done;
    }

    private static string CodeFor(UnlockerTarget target) => target.Client.ToString().ToLowerInvariant();

    /// <summary>
    /// The one place a terminal failure is built, so every one of them is logged at Error rather
    /// than the log simply stopping mid-run with no reason. The message logged is exactly the one
    /// the result carries.
    /// </summary>
    private UnlockerResult Failed(string message, string? code, IReadOnlyList<string>? warnings = null)
    {
        _log.Write(LogLine.Error(message, code));
        return UnlockerResult.Fail(message, warnings);
    }

    public async Task<UnlockerResult> InstallAsync(UnlockerTarget target, IUnlockerAssetSource assets,
                                                  IProgress<UnlockerProgress> progress,
                                                  CancellationToken ct)
    {
        var ea = target.Client == ClientKind.EaApp;
        var code = CodeFor(target);
        var steps = new Steps(progress, ea ? InstallStepsEaApp : InstallStepsOrigin, _log, code);
        var warnings = new List<string>();

        // Step 1. Checked before anything is mutated: without it the config files and the
        // autostart change land and only the DLL copy into Program Files fails, leaving a
        // half-installed unlocker.
        steps.Begin("Checking administrator rights");
        if (!host.IsElevated) return UnlockerResult.NeedsElevation();
        steps.Done();

        // Step 2. Before any mutation, so a digest mismatch or a dead mirror changes nothing.
        steps.Begin("Fetching the unlocker");
        ReadOnlyMemory<byte> dll;
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
            return Failed(e.Message, code);
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
                return Failed(
                    $"These processes are still running and hold the client directory open: " +
                    $"{string.Join(", ", survivors)}. Close them and try again.", code);

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
            return Failed($"'{paths.ConfigDirectory}' could not be created: {e.Message}", code);
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
            return Failed($"The unlocker configuration could not be written: {e.Message}", code);
        }
        steps.Done();

        return await InstallRestAsync(target, dll, steps, warnings, ea, ct);
    }

    private async Task<UnlockerResult> InstallRestAsync(UnlockerTarget target,
                                                       ReadOnlyMemory<byte> dll, Steps steps,
                                                       List<string> warnings, bool ea,
                                                       CancellationToken ct)
    {
        var code = CodeFor(target);

        // Step 6, non-fatal: nothing to remove is the normal case.
        steps.Begin("Removing older unlocker files");
        foreach (var name in new[] { "version_o.dll", "winhttp.dll", "winhttp_o.dll" })
            TryDelete(Path.Combine(target.ClientPath, name), warnings, code);
        try
        {
            foreach (var stale in Directory.GetFiles(target.ClientPath, "w_*.ini"))
                TryDelete(stale, warnings, code);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var message = $"Older unlocker .ini files could not be listed: {e.Message}";
            warnings.Add(message);
            _log.Write(LogLine.Warning(message, code));
        }
        steps.Done();

        // Step 7, fatal: without the DLL on disk nothing is installed, whatever else succeeded.
        steps.Begin($"Copying {DllName}");
        var installed = Path.Combine(target.ClientPath, DllName);
        try
        {
            // Written from the verified bytes rather than copied from the cache, and atomically: a
            // torn version.dll is one EA Desktop cannot load, and File.Exists would still call it
            // installed.
            await AtomicFile.WriteAllBytesAsync(installed, dll, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failed(
                $"{DllName} could not be written to '{target.ClientPath}': {e.Message}", code, warnings);
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
                await AtomicFile.WriteAllBytesAsync(Path.Combine(staged, DllName), dll, ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                var message = $"The StagedEADesktop copy failed, so the unlocker will need " +
                             $"reinstalling after EA app updates itself: {e.Message}";
                warnings.Add(message);
                _log.Write(LogLine.Warning(message, code));
            }
            steps.Done();

            // Step 9, non-fatal: the flag is a convenience and the install stands without it.
            steps.Begin("Updating machine.ini");
            try
            {
                if (!await IniFlagEditor.AddFlagAsync(paths.MachineIniFile, MachineIniFlag, ct)
                    && !File.Exists(paths.MachineIniFile))
                {
                    var message = $"machine.ini was not found at '{paths.MachineIniFile}', so it was " +
                                 "left alone. This is not critical.";
                    warnings.Add(message);
                    _log.Write(LogLine.Warning(message, code));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                var message = $"machine.ini could not be updated: {e.Message}";
                warnings.Add(message);
                _log.Write(LogLine.Warning(message, code));
            }
            steps.Done();
        }

        // Step 10, non-fatal and reversible: the previous value is backed up first so removal can
        // put it back, rather than leaving the user's login behaviour changed.
        steps.Begin("Updating autostart");

        var ownsBackup = false;
        try
        {
            // Seeded from this client's existing record, never recomputed from nothing. A repair
            // install finds the value already removed, so a freshly computed flag would be false
            // and would overwrite the owning record with a disowning one, after which nothing ever
            // restores the user's autostart entry. Inside the try because this step is documented
            // non-fatal and the store only swallows I/O and JSON faults, so anything else escaping
            // here would fail the install after version.dll is already on disk.
            ownsBackup = _records.Read(Scope, target.Client,
                                      expectedClientPath: target.ClientPath)?.OwnsAutostartBackup == true;

            var existing = host.ReadAutostartValue(AutostartValueName);

            // Written when no backup exists yet, and refreshed when this client already owns the
            // one on disk. The value is one machine-wide entry, so the first client to install
            // owns it and a second client must never overwrite that backup: doing so would record
            // the already-removed state and removal would restore nothing. But an owner has to
            // re-capture, because the client can re-create its Run entry between two installs and
            // a repair install that kept the older backup would delete the newer value and put
            // the stale command line back.
            if (existing is not null
                && (ownsBackup || !File.Exists(appPaths.UnlockerAutostartBackupFile)))
            {
                Directory.CreateDirectory(appPaths.Root);
                await AtomicFile.WriteAllTextAsync(appPaths.UnlockerAutostartBackupFile,
                    System.Text.Json.JsonSerializer.Serialize(
                        new AutostartBackup(AutostartValueName, existing.Value, existing.Kind)), ct);
                ownsBackup = true;
            }

            // Never delete a value nothing has a backup of. Reaching the else means a live value
            // and a backup file this install does not own, so another install already owns the
            // saved entry and this one leaves the value alone rather than deleting something only
            // that other install's removal could put back.
            if (existing is not null && ownsBackup)
            {
                host.RemoveAutostartValue(AutostartValueName);
                _log.Write(LogLine.Info("Autostart entry recorded and removed", code));
            }
            else if (existing is not null)
            {
                var message = "Another install of the unlocker already owns the saved autostart " +
                             "entry, so this one was left as it is.";
                warnings.Add(message);
                _log.Write(LogLine.Warning(message, code));
            }
        }
        // SecurityException is here on purpose: the host guards its registry READS but not its
        // writes, because it holds no policy, and a group-policy-restricted Run key throws exactly
        // this. Without it this non-fatal step would abort the install after the DLL is already in
        // place.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or System.Security.SecurityException)
        {
            var message = $"The autostart entry could not be changed: {e.Message}";
            warnings.Add(message);
            _log.Write(LogLine.Warning(message, code));
        }
        steps.Done();

        // Written last, so a record only ever describes an install that reached the end. Removal
        // reads it to decide what is its to undo.
        try
        {
            await _records.WriteAsync(Scope,
                new UnlockerInstallRecord(target.ClientPath, target.Client, ownsBackup), ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var message = $"The install record could not be written, so a later removal may not " +
                         $"restore everything: {e.Message}";
            warnings.Add(message);
            _log.Write(LogLine.Warning(message, code));
        }

        steps.Finish("Done");
        return UnlockerResult.Ok(warnings);
    }

    private void TryDelete(string path, List<string> warnings, string? code)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var message = $"'{Path.GetFileName(path)}' could not be removed: {e.Message}";
            warnings.Add(message);
            _log.Write(LogLine.Warning(message, code));
        }
    }

    internal sealed record AutostartBackup(string Name, string Value, AutostartValueKind Kind);

    internal const int RemoveStepsEaApp = 9;
    internal const int RemoveStepsOrigin = 6;

    public async Task<UnlockerResult> RemoveAsync(UnlockerTarget target,
                                                 IProgress<UnlockerProgress> progress,
                                                 CancellationToken ct)
    {
        var ea = target.Client == ClientKind.EaApp;
        var code = CodeFor(target);
        var steps = new Steps(progress, ea ? RemoveStepsEaApp : RemoveStepsOrigin, _log, code);
        var warnings = new List<string>();

        // Step 1, before the elevation check on purpose: a machine with nothing installed must not
        // raise a UAC prompt to tell the user there is nothing to do. "Nothing installed" is not
        // just an absent DLL, because an EA app self-update can delete it on its own (which is why
        // the StagedEADesktop copy exists) and install also flipped the autostart value and wrote a
        // backup.
        steps.Begin("Checking what is installed");
        var installed = Path.Combine(target.ClientPath, DllName);

        // This client's own artefacts, plus one narrow fallback. The configuration directory and
        // the autostart backup are shared, so testing those answered "something to remove" for a
        // client with nothing installed as soon as any other client was.
        //
        // The fallback covers a machine installed by a build that had no records at all: an EA app
        // self-update can delete version.dll on its own, so such a machine can have a live
        // autostart backup and no DLL and no record, and without this it reports "nothing to
        // remove" and the user's Run value stays deleted for good. `legacy` is only true when the
        // store can prove there are no records in this scope at all.
        var record = _records.Read(Scope, target.Client, expectedClientPath: target.ClientPath);
        var others = _records.Others(Scope, target.Client);
        var legacy = record is null && others.Complete && others.Records.Count == 0;
        var somethingToRemove = File.Exists(installed) || record is not null
                                || (legacy && File.Exists(appPaths.UnlockerAutostartBackupFile));
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
                return Failed(
                    $"These processes are still running and hold the client directory open: " +
                    $"{string.Join(", ", survivors)}. Close them and try again.", code);

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
                var message = $"The '{ScheduledTaskName}' scheduled task could not be removed: {e.Message}";
                warnings.Add(message);
                _log.Write(LogLine.Warning(message, code));
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
            return Failed($"{DllName} could not be removed from " +
                          $"'{target.ClientPath}': {e.Message}", code, warnings);
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
                    TryDelete(Path.Combine(inner, DllName), warnings, code);

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
                var message = $"StagedEADesktop could not be cleaned up: {e.Message}";
                warnings.Add(message);
                _log.Write(LogLine.Warning(message, code));
            }
            steps.Done();

            steps.Begin("Restoring machine.ini");
            try
            {
                await IniFlagEditor.RemoveFlagAsync(paths.MachineIniFile, MachineIniFlag, ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                var message = $"machine.ini could not be restored: {e.Message}";
                warnings.Add(message);
                _log.Write(LogLine.Warning(message, code));
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

            // Only the install that captured the value restores it. Another client's removal
            // putting it back would re-enable the user's login entry while that client's unlocker
            // is still installed.
            //
            // No record covers three cases, not one: an install made before this version, which
            // cannot have a record and is therefore the only possible claimant; a record that
            // could not be read; and a record naming a different client path. All three are
            // treated as "may restore" on purpose. The bias is deliberate: restoring early puts
            // a login entry back sooner than the user expects and they can turn it off again,
            // whereas never restoring loses the value for good with nothing left to recover it
            // from.
            //
            // The last removal in the scope may also claim it, which is what covers a pre-record
            // owner: it left a backup and no record, so once every recorded client is gone the
            // backup would otherwise sit there for ever with nobody entitled to restore it.
            var mayRestore = record is null || record.OwnsAutostartBackup
                             || (others.Complete && others.Records.Count == 0);
            if (mayRestore && File.Exists(backupFile))
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
                    var message = $"The autostart backup could not be read, so it was discarded: {e.Message}";
                    warnings.Add(message);
                    _log.Write(LogLine.Warning(message, code));
                }

                if (string.IsNullOrEmpty(backup?.Name) || backup.Value is null)
                {
                    if (backup is not null)
                    {
                        const string message = "The autostart backup was incomplete, so it was discarded.";
                        warnings.Add(message);
                        _log.Write(LogLine.Warning(message, code));
                    }
                }
                // The client can re-create its own Run entry with a newer path between install and
                // removal, and restoring the recorded value over that would downgrade the user's
                // autostart to a stale command line.
                else if (host.ReadAutostartValue(backup.Name) is not null)
                {
                    warnings.Add("The autostart entry had been re-created, so it was left as it is.");
                    _log.Write(LogLine.Warning(
                        "Autostart entry had been re-created, so it was left as it is", code));
                }
                else if (backup.Kind == AutostartValueKind.Unsupported)
                {
                    // Removed on install because the entry is deleted type-blind, but it was never
                    // text, so writing it back as text would change what the client reads.
                    warnings.Add("The autostart entry was not a text value, so it could not be put back.");
                    _log.Write(LogLine.Warning(
                        "Autostart entry was not a text value, so it could not be put back", code));
                }
                else
                {
                    host.WriteAutostartValue(backup.Name,
                        new AutostartValue(backup.Value, backup.Kind));
                    _log.Write(LogLine.Info("Autostart entry restored", code));
                }

                File.Delete(backupFile);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or System.Security.SecurityException)
        {
            var message = $"The autostart entry could not be restored: {e.Message}";
            warnings.Add(message);
            _log.Write(LogLine.Warning(message, code));
        }
        steps.Done();

        steps.Begin("Removing the unlocker configuration");
        try
        {
            // Shared by every client in this scope, and it is the unlocker DLL's own path rather
            // than ours, so it cannot be split per client. Deleting it while another client's
            // unlocker is installed leaves that DLL loading and finding no configuration.
            // `others` was read once at step 1; nothing between there and here writes a record.
            // Not deleted unless the store could actually prove nobody else needs it. An
            // unreadable store or one corrupt sibling record answers "cannot tell", and treating
            // that as "nobody" destroys the other client's configuration exactly as an
            // unconditional delete of the shared directory would.
            //
            // The records are not the whole answer, and for one shape they are no answer at all.
            // No install made by the shipped build wrote a record, so on the entire existing
            // two-client population the store answers "nobody else" with complete confidence
            // while the other client's DLL is on disk. And the store keys a record by ClientKind,
            // so it cannot see a sibling of the SAME kind at another path at all: two EA app
            // directories share one record file, and Others() excludes the caller's own kind by
            // construction. In both shapes the DLL on disk is the only ground truth, so the
            // sibling test is by PATH rather than by kind.
            var self = PathIdentity.Canonical(target.ClientPath) ?? target.ClientPath;
            var siblingInstalled = (await DetectTargetsAsync(ct)).Any(t =>
                !string.Equals(PathIdentity.Canonical(t.ClientPath) ?? t.ClientPath, self,
                               StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(t.ClientPath, DllName)));

            if (!others.Complete)
            {
                var message = "Whether another client still needs the unlocker configuration could " +
                             "not be determined, so the configuration folder was left in place.";
                warnings.Add(message);
                _log.Write(LogLine.Warning(message, code));
            }
            else if (others.Records.Count == 0 && !siblingInstalled
                     && Directory.Exists(paths.ConfigDirectory))
                Directory.Delete(paths.ConfigDirectory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var message = $"The unlocker configuration folder could not be removed: {e.Message}";
            warnings.Add(message);
            _log.Write(LogLine.Warning(message, code));
        }
        steps.Done();

        // After the configuration decision above, which asks the store what else is installed.
        _records.Delete(Scope, target.Client);

        steps.Finish("Done");
        return UnlockerResult.Ok(warnings);
    }
}
