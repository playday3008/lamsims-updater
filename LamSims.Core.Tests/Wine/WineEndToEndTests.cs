using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.Core.Settings;
using LamSims.Core.Unlocking;
using LamSims.Core.Unlocking.Wine;

namespace LamSims.Core.Tests;

/// <summary>
/// A real Lutris config naming a real fake prefix, discovered rather than handed over, driven
/// through the real engine with a fake asset source. Nothing here is a stub except the DLL bytes and
/// the process reader: this is the only test that proves discovery, resolution, the engine and the
/// override are wired to each other rather than merely correct on their own.
/// </summary>
public class WineEndToEndTests
{
    [LinuxFact]
    public async Task A_discovered_prefix_installs_and_removes_cleanly()
    {
        using var dir = new TempDir();

        var home = Path.Combine(dir.Path, "home");
        var prefixRoot = Path.Combine(dir.Path, "mnt", "games", "ea-app");
        WinePrefixScannerTests.Prefix(prefixRoot);
        Directory.CreateDirectory(Path.Combine(prefixRoot, "drive_c", "ProgramData"));
        // PathsFor requires both the user directory and its AppData\Roaming subdirectory: AppData is an
        // intermediate segment in the path resolution, so ResolveUnder returns null if it is missing,
        // which causes Inner to return null and kills detection before targets are produced.
        Directory.CreateDirectory(Path.Combine(prefixRoot, "drive_c", "users", "playday", "AppData", "Roaming"));

        const string windows =
            @"C:\Program Files\Electronic Arts\EA Desktop\EA Desktop\EADesktop.exe";
        var clientDirectory = Path.Combine(prefixRoot, "drive_c", "Program Files",
                                           "Electronic Arts", "EA Desktop", "EA Desktop");
        Directory.CreateDirectory(clientDirectory);
        File.WriteAllBytes(Path.Combine(clientDirectory, "EADesktop.exe"), [0x4D, 0x5A]);

        File.WriteAllText(Path.Combine(prefixRoot, "system.reg"),
            "WINE REGISTRY Version 2\n#arch=win64\n\n[Software\\\\Electronic Arts\\\\EA Desktop] 0\n"
            + $"\"ClientPath\"=\"{windows.Replace("\\", "\\\\")}\"\n");

        const string emptyBlock =
            "WINE REGISTRY Version 2\n#arch=win64\n\n[Software\\\\Wine\\\\DllOverrides] 0\n";
        var userReg = Path.Combine(prefixRoot, "user.reg");
        File.WriteAllText(userReg, emptyBlock);

        // Discovery has to find the prefix from this file alone. Nothing tells the scanner the path.
        var games = Path.Combine(home, ".local", "share", "lutris", "games");
        Directory.CreateDirectory(games);
        File.WriteAllText(Path.Combine(games, "ea-app-1778070803.yml"),
                          $"name: EA app\ngame_slug: ea-app\nprefix: {prefixRoot}\nrunner: wine\n");

        var app = new AppPaths(Path.Combine(dir.Path, "app"));
        app.EnsureCreated();

        var source = Path.Combine(dir.Path, "cache", "version.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, [0x4D, 0x5A, 0x90, 0x00]);

        var notes = new Notes();
        var backend = new WinePrefixUnlockerBackend(
            new LauncherHomes(home, null, null, null), app, new FakeDelayProvider(),
            new FakeWineProcesses(), "playday", () => null, notes);

        var targets = await backend.DetectTargetsAsync(CancellationToken.None);

        Assert.Single(targets);
        Assert.Equal("EA app", targets[0].Environment!.Detail);
        Assert.Equal(EnvironmentSource.Lutris, targets[0].Environment!.Source);

        var progress = new SyncProgress<UnlockerProgress>(_ => { });
        var installed = await backend.InstallAsync(targets[0], new StubAssetSource(source),
                                                   progress, CancellationToken.None);

        Assert.True(installed.Success, installed.Error);
        Assert.True(File.Exists(Path.Combine(clientDirectory, "version.dll")));
        Assert.Equal("native,builtin",
                     WineRegistryFile.ReadValue(userReg, WineDllOverride.Key, "*version")?.Text);
        Assert.Equal(UnlockerState.Installed,
                     (await backend.GetStatusAsync(targets[0], CancellationToken.None)).State);

        var removed = await backend.RemoveAsync(targets[0], progress, CancellationToken.None);

        Assert.True(removed.Success, removed.Error);
        Assert.False(File.Exists(Path.Combine(clientDirectory, "version.dll")));
        Assert.Equal(emptyBlock, await File.ReadAllTextAsync(userReg));
        Assert.Equal(UnlockerState.NotInstalled,
                     (await backend.GetStatusAsync(targets[0], CancellationToken.None)).State);
    }

    /// <summary>
    /// Detection on a machine with no prefixes at all is empty and quiet — not an error. A build
    /// that bannered here would nag every Windows user and every Linux user who has no Wine.
    /// </summary>
    [LinuxFact]
    public async Task A_machine_with_no_prefixes_detects_nothing_and_does_not_fail()
    {
        using var dir = new TempDir();
        var app = new AppPaths(Path.Combine(dir.Path, "app"));
        app.EnsureCreated();

        var notes = new Notes();
        var backend = new WinePrefixUnlockerBackend(
            new LauncherHomes(Path.Combine(dir.Path, "empty-home"), null, null, null), app,
            new FakeDelayProvider(), new FakeWineProcesses(), "playday", () => null, notes);

        Assert.Empty(await backend.DetectTargetsAsync(CancellationToken.None));

        // No REJECTION notes: nothing was examined, so nothing can be complained about. Not
        // Assert.Empty on the whole list — the scanner's sandbox diagnostic keys off /.flatpak-info,
        // which exists when the suite itself runs inside a Flatpak, and that note is correct there.
        Assert.DoesNotContain(notes.Lines, l => l.Contains("is not a Wine prefix", StringComparison.Ordinal));
        Assert.DoesNotContain(notes.Lines, l => l.Contains("no EA app or Origin", StringComparison.Ordinal));
    }
}
