using System.Runtime.Versioning;
using LamSims.Core.Settings;
using LamSims.Core.Unlocking;

namespace LamSims.Core.Tests;

public sealed class InstallFixture : IDisposable
{
    public TempDir Dir { get; } = new();
    public FakeUnlockerHost Host { get; } = new();
    public FakeDelayProvider Delays { get; } = new();
    public List<UnlockerProgress> Reports { get; } = [];
    public UnlockerPaths Paths { get; }
    public AppPaths App { get; }
    public EaClientUnlockerBackend Backend { get; }
    public IUnlockerAssetSource Assets { get; }
    public string DllSource { get; }

    public InstallFixture(ClientKind kind = ClientKind.EaApp)
    {
        Paths = new UnlockerPaths(Path.Combine(Dir.Path, "roaming"), Path.Combine(Dir.Path, "common"));

        // Removal deletes the unlocker's own config directory but leaves the "anadius" folder above
        // it, which may hold other anadius tools. Pre-created so a round trip starts from that state.
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.ConfigDirectory)!);
        App = new AppPaths(Path.Combine(Dir.Path, "app"));
        App.EnsureCreated();

        // The client lives two levels down so the StagedEADesktop sibling has somewhere to go.
        var clientDir = Path.Combine(Dir.Path, "Program Files", "EA Desktop", "EA Desktop");
        Directory.CreateDirectory(clientDir);
        Host.ClientPaths[kind == ClientKind.EaApp
            ? ClientRegistryKey.EaDesktop
            : ClientRegistryKey.Origin] = Path.Combine(clientDir, "EADesktop.exe");

        DllSource = Path.Combine(Dir.Path, "cache", "ea_app_version.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(DllSource)!);
        File.WriteAllBytes(DllSource, [0x4D, 0x5A, 0x90, 0x00]);
        Assets = new StubAssetSource(DllSource);

        Backend = new EaClientUnlockerBackend(Host, Paths, App, Delays);
    }

    public Task<UnlockerTarget> TargetAsync() =>
        Backend.DetectTargetsAsync(CancellationToken.None).ContinueWith(t => t.Result[0]);

    public Task<UnlockerResult> InstallAsync(UnlockerTarget target) =>
        Backend.InstallAsync(target, Assets, new SyncProgress<UnlockerProgress>(Reports.Add),
                             CancellationToken.None);

    /// <summary>The roots an aborted install must leave unchanged. DownloadPaths is excluded: a
    /// verified DLL may legitimately have been cached before the step under test refused.</summary>
    public string[] GuardedRoots =>
    [
        Paths.Roaming,
        Paths.CommonAppData,
        Path.Combine(Dir.Path, "Program Files"),
    ];

    public string Snapshot() => string.Join("\n", GuardedRoots
        .Where(Directory.Exists)
        .SelectMany(r => Directory.GetFileSystemEntries(r, "*", SearchOption.AllDirectories))
        .Order());

    public void Dispose() => Dir.Dispose();
}

public sealed class StubAssetSource(string path) : IUnlockerAssetSource
{
    public int Calls { get; private set; }

    public Task<string> GetDllAsync(ClientKind client, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(path);
    }
}

public class EaClientInstallTests
{
    // The asset-source call count is asserted too: the result flag alone would pass for an install
    // that half-ran before refusing.
    [Fact]
    public async Task Without_elevation_nothing_is_touched_and_no_asset_is_fetched()
    {
        using var f = new InstallFixture();
        f.Host.IsElevated = false;
        var target = await f.TargetAsync();
        var before = f.Snapshot();

        var result = await f.InstallAsync(target);

        Assert.False(result.Success);
        Assert.True(result.RequiresElevation);
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(0, ((StubAssetSource)f.Assets).Calls);
    }

    // The config directory is step 4, the first mutation the abort has to prevent. The download
    // directory is outside GuardedRoots because step 2 may already have cached the DLL.
    [Fact]
    public async Task A_client_that_outlives_the_kill_timeout_aborts_before_anything_is_written()
    {
        using var f = new InstallFixture();

        // Both lists are needed: the step only tries to kill when RunningClientProcesses reports
        // something, so seeding Survivors alone leaves the kill unreached and the install succeeds.
        f.Host.Running.Add("EADesktop");
        f.Host.Survivors.Add("EADesktop");
        var target = await f.TargetAsync();

        var result = await f.InstallAsync(target);

        Assert.False(result.Success);
        Assert.Contains("EADesktop", result.Error);
        Assert.False(Directory.Exists(f.Paths.ConfigDirectory));
    }

    [Fact]
    public async Task The_post_kill_settle_is_awaited_through_the_delay_provider_not_slept()
    {
        using var f = new InstallFixture();
        f.Host.Running.Add("EADesktop");
        var target = await f.TargetAsync();

        await f.InstallAsync(target);

        Assert.NotEmpty(f.Delays.Delays);
    }

    // A group-policy-restricted Run key throws SecurityException, which the host does not catch.
    // The backend's filter has to, or the non-fatal autostart step aborts the install after the
    // DLL is already in place.
    [Fact]
    public async Task A_registry_write_the_policy_forbids_warns_instead_of_aborting_the_install()
    {
        using var f = new InstallFixture();
        f.Host.AutostartValues["EADM"] = @"C:\EA\EADesktop.exe";
        f.Host.ThrowSecurityOnAutostartWrite = true;
        var target = await f.TargetAsync();

        var result = await f.InstallAsync(target);

        // Success alone would pass against a step that swallowed the failure; the warning and the
        // DLL are both checked.
        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Warnings ?? [], w => w.Contains("autostart"));
        Assert.True(File.Exists(Path.Combine(target.ClientPath, "version.dll")));
    }

    /// <summary>
    /// Upstream selected processes with a case-insensitive ProcessName.StartsWith("EA"), which kills
    /// unrelated software such as EarTrumpet. This covers the set of names the backend asks about;
    /// the equality matching itself lives in WindowsUnlockerHost, which no test executes.
    /// </summary>
    [Theory]
    [InlineData(ClientKind.EaApp)]
    [InlineData(ClientKind.Origin)]
    public async Task The_client_is_matched_by_exact_process_name_never_by_prefix(ClientKind kind)
    {
        using var f = new InstallFixture(kind);
        f.Host.Running.Add("whatever");
        var target = await f.TargetAsync();

        await f.InstallAsync(target);

        var expected = EaClientUnlockerBackend.ProcessNames[kind];
        Assert.NotEmpty(f.Host.Asked);
        Assert.Equal(expected, f.Host.Asked[0]);

        // "Origin" belongs, being a real process name; a bare "EA" would mean prefix matching is back.
        Assert.DoesNotContain("EA", expected);
        Assert.DoesNotContain(expected, n => n.Contains("lamsims", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_successful_install_writes_both_config_files()
    {
        using var f = new InstallFixture();
        var target = await f.TargetAsync();

        var result = await f.InstallAsync(target);

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(f.Paths.ConfigFile));
        Assert.True(File.Exists(f.Paths.DlcListFile));
    }

    [Theory]
    [InlineData(ClientKind.EaApp, 10)]
    [InlineData(ClientKind.Origin, 8)]
    public async Task Every_progress_report_carries_the_right_total(ClientKind kind, int total)
    {
        using var f = new InstallFixture(kind);
        var target = await f.TargetAsync();

        await f.InstallAsync(target);

        Assert.NotEmpty(f.Reports);
        Assert.All(f.Reports, r => Assert.Equal(total, r.Total));
        Assert.Equal(Enumerable.Range(0, f.Reports.Count), f.Reports.Select(r => r.Completed));
    }

    // Upstream deleted its source DLL before staging it, so the staged copy never happened. The
    // cache entry is checked alongside both copies.
    [Fact]
    public async Task An_ea_app_install_places_the_dll_in_both_locations_and_keeps_the_cache()
    {
        using var f = new InstallFixture();
        var target = await f.TargetAsync();
        var expected = await File.ReadAllBytesAsync(f.DllSource);

        var result = await f.InstallAsync(target);
        Assert.True(result.Success, result.Error);

        var installed = Path.Combine(target.ClientPath, "version.dll");
        var staged = Path.Combine(Directory.GetParent(target.ClientPath)!.FullName,
                                  "StagedEADesktop", "EA Desktop", "version.dll");

        Assert.Equal(expected, await File.ReadAllBytesAsync(installed));
        Assert.Equal(expected, await File.ReadAllBytesAsync(staged));
        Assert.True(File.Exists(f.DllSource), "the cache entry was deleted");
    }

    [Fact]
    public async Task An_origin_install_does_not_create_a_staged_folder()
    {
        using var f = new InstallFixture(ClientKind.Origin);
        var target = await f.TargetAsync();

        Assert.True((await f.InstallAsync(target)).Success);

        Assert.False(Directory.Exists(Path.Combine(
            Directory.GetParent(target.ClientPath)!.FullName, "StagedEADesktop")));
    }

    [Fact]
    public async Task Old_unlocker_files_are_removed_from_the_client_directory()
    {
        using var f = new InstallFixture();
        var target = await f.TargetAsync();
        foreach (var name in new[] { "version_o.dll", "winhttp.dll", "winhttp_o.dll", "w_stale.ini" })
            await File.WriteAllTextAsync(Path.Combine(target.ClientPath, name), "old");

        Assert.True((await f.InstallAsync(target)).Success);

        Assert.Empty(Directory.GetFiles(target.ClientPath, "w_*.ini"));
        Assert.False(File.Exists(Path.Combine(target.ClientPath, "version_o.dll")));
        Assert.False(File.Exists(Path.Combine(target.ClientPath, "winhttp.dll")));
        Assert.False(File.Exists(Path.Combine(target.ClientPath, "winhttp_o.dll")));
    }

    [Fact]
    public async Task An_existing_machine_ini_gains_the_flag()
    {
        using var f = new InstallFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(f.Paths.MachineIniFile)!);
        await File.WriteAllTextAsync(f.Paths.MachineIniFile, "[machine]\nfoo=1\n");
        var target = await f.TargetAsync();

        Assert.True((await f.InstallAsync(target)).Success);

        Assert.Contains("machine.bgsstandaloneenabled=0",
                        await File.ReadAllTextAsync(f.Paths.MachineIniFile));
    }

    // A missing machine.ini is the common case where EA Desktop has never run. The warning is
    // asserted as well, since success alone would pass against a step that did nothing.
    [Fact]
    public async Task A_missing_machine_ini_warns_without_failing_the_install()
    {
        using var f = new InstallFixture();
        var target = await f.TargetAsync();

        var result = await f.InstallAsync(target);

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Warnings ?? [], w => w.Contains("machine.ini"));
    }

    // The backup lands under AppPaths, not in the config directory that removal deletes.
    [Fact]
    public async Task The_autostart_value_is_recorded_before_it_is_removed()
    {
        using var f = new InstallFixture();
        f.Host.AutostartValues["EADM"] = @"C:\EA\EADesktop.exe -silent";
        var target = await f.TargetAsync();

        Assert.True((await f.InstallAsync(target)).Success);

        Assert.DoesNotContain("EADM", f.Host.AutostartValues.Keys);
        Assert.True(File.Exists(f.App.UnlockerAutostartBackupFile));
        Assert.Contains("-silent", await File.ReadAllTextAsync(f.App.UnlockerAutostartBackupFile));
    }

    [Fact]
    public async Task No_autostart_value_means_no_backup_file_and_no_failure()
    {
        using var f = new InstallFixture();
        var target = await f.TargetAsync();

        Assert.True((await f.InstallAsync(target)).Success);

        Assert.False(File.Exists(f.App.UnlockerAutostartBackupFile));
    }

    /// <summary>
    /// The count alone passes against an off-by-one that never reaches the end, and the final value
    /// alone passes against a run that skipped steps, so contiguity is checked too.
    /// </summary>
    [Theory]
    [InlineData(ClientKind.EaApp, 10)]
    [InlineData(ClientKind.Origin, 8)]
    public async Task Progress_reports_run_from_zero_to_total(ClientKind kind, int total)
    {
        using var f = new InstallFixture(kind);
        var target = await f.TargetAsync();

        var result = await f.InstallAsync(target);
        Assert.True(result.Success, result.Error);

        Assert.All(f.Reports, r => Assert.Equal(total, r.Total));
        Assert.Equal(total + 1, f.Reports.Count);
        Assert.Equal(Enumerable.Range(0, total + 1), f.Reports.Select(r => r.Completed));
        Assert.Equal(total, f.Reports[^1].Completed);
    }

    // Upstream's CopyDllFile swallows UnauthorizedAccessException, so InstallUnlocker reports
    // success with no DLL on disk.
    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_client_directory_that_cannot_be_written_fails_rather_than_reporting_success()
    {
        // SetUnixFileMode is a no-op on Windows, where the directory would stay writable and the
        // copy would succeed.
        if (OperatingSystem.IsWindows()) return;

        using var f = new InstallFixture();
        var target = await f.TargetAsync();

        // ReadOnlyDir creates and locks parent/read-only, leaving the directory you hand it
        // writable, so the destination is locked directly here instead.
        const UnixFileMode locked = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        var unlocked = File.GetUnixFileMode(target.ClientPath);
        File.SetUnixFileMode(target.ClientPath, locked);

        try
        {
            var result = await f.InstallAsync(target);

            Assert.False(result.Success);
            Assert.False(File.Exists(Path.Combine(target.ClientPath, "version.dll")));
        }
        finally
        {
            // Restore before the fixture disposes, or TempDir cannot delete the tree.
            File.SetUnixFileMode(target.ClientPath, unlocked);
        }
    }
}
