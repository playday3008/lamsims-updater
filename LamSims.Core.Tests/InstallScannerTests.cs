using System.Runtime.Versioning;
using LamSims.Core.Catalogs;
using LamSims.Core.Scanning;

namespace LamSims.Core.Tests;

public class InstallScannerTests
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static PackEntry Pack(string code, params string[] installDirs) => new(
        code, "Pack " + code, PackType.Expansion, 1024, null, Digest,
        new[] { new Uri($"https://host-a.example.invalid/{code}.zip") },
        installDirs.Length == 0 ? new[] { code } : installDirs);

    [Fact]
    public void Reports_a_pack_whose_directory_is_present_as_installed()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "EP01"));

        var result = Assert.Single(InstallScanner.Scan(temp.Path, new[] { Pack("EP01") }).Packs);

        Assert.Equal(PackInstallState.Installed, result.State);
        Assert.Empty(result.MissingDirs);
    }

    [Fact]
    public void Reports_a_pack_with_no_directory_as_not_installed()
    {
        using var temp = new TempDir();

        var result = Assert.Single(InstallScanner.Scan(temp.Path, new[] { Pack("EP01") }).Packs);

        Assert.Equal(PackInstallState.NotInstalled, result.State);
        Assert.Equal("EP01", Assert.Single(result.MissingDirs));
    }

    [Fact]
    public void Reports_a_pack_missing_one_of_its_directories_as_partial()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "SP30"));

        var result = Assert.Single(InstallScanner.Scan(temp.Path, new[] { Pack("SP30", "SP30", "SP30_Delta") }).Packs);

        // A subset means an interrupted install, which needs a reinstall rather than the skip
        // "installed" would earn it.
        Assert.Equal(PackInstallState.Partial, result.State);
        Assert.Equal("SP30_Delta", Assert.Single(result.MissingDirs));
    }

    [Fact]
    public void Compares_directory_names_and_not_the_whole_path()
    {
        using var temp = new TempDir();
        var game = Path.Combine(temp.Path, "SP20", "The Sims 4");
        Directory.CreateDirectory(game);

        var result = Assert.Single(InstallScanner.Scan(game, new[] { Pack("SP20") }).Packs);

        // Upstream's substring match against the full path called this installed.
        Assert.Equal(PackInstallState.NotInstalled, result.State);
    }

    [Fact]
    public void Ignores_files_that_share_a_pack_name()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "EP01"), "not a directory");

        var result = Assert.Single(InstallScanner.Scan(temp.Path, new[] { Pack("EP01") }).Packs);

        Assert.Equal(PackInstallState.NotInstalled, result.State);
    }

    [Fact]
    public void Matches_directory_names_case_insensitively()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "ep01"));

        var result = Assert.Single(InstallScanner.Scan(temp.Path, new[] { Pack("EP01") }).Packs);

        Assert.Equal(PackInstallState.Installed, result.State);
    }

    [Fact]
    public void Reports_every_pack_as_not_installed_when_the_game_directory_is_absent()
    {
        using var temp = new TempDir();

        var scan = InstallScanner.Scan(Path.Combine(temp.Path, "absent"), new[] { Pack("EP01"), Pack("EP02") });

        Assert.Equal(2, scan.Packs.Count);
        Assert.All(scan.Packs, r => Assert.Equal(PackInstallState.NotInstalled, r.State));
        Assert.False(scan.GameDirectoryReadable);
    }

    [Fact]
    public void Reports_the_game_directory_as_readable_when_it_exists_and_is_empty()
    {
        using var temp = new TempDir();

        var scan = InstallScanner.Scan(temp.Path, new[] { Pack("EP01") });

        // Empty and unreadable both leave every pack NotInstalled; only this flag tells them
        // apart.
        Assert.True(scan.GameDirectoryReadable);
        Assert.Equal(PackInstallState.NotInstalled, Assert.Single(scan.Packs).State);
    }

    [Fact]
    public void Keeps_the_order_of_the_catalog()
    {
        using var temp = new TempDir();

        var scan = InstallScanner.Scan(temp.Path, new[] { Pack("EP02"), Pack("EP01") });

        Assert.Equal(new[] { "EP02", "EP01" }, scan.Packs.Select(r => r.Code));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void Reports_every_pack_as_not_installed_when_the_game_directory_cannot_be_read()
    {
        if (OperatingSystem.IsWindows()) return;

        using var temp = new TempDir();
        var gameDir = Path.Combine(temp.Path, "game");

        // Created before the directory is locked down: a readable gameDir would report EP01
        // as Installed, so this is what actually distinguishes "unreadable" from "empty" —
        // without it the test would pass the same way under a CI user with permission to
        // read through the chmod (e.g. root).
        Directory.CreateDirectory(Path.Combine(gameDir, "EP01"));

        var unreadableMode = System.IO.UnixFileMode.UserWrite;
        var readableMode = System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite | System.IO.UnixFileMode.UserExecute;

        try
        {
            System.IO.File.SetUnixFileMode(gameDir, unreadableMode);
            var scan = InstallScanner.Scan(gameDir, new[] { Pack("EP01"), Pack("EP02") });

            Assert.Equal(2, scan.Packs.Count);
            Assert.All(scan.Packs, r => Assert.Equal(PackInstallState.NotInstalled, r.State));
            Assert.False(scan.GameDirectoryReadable);
        }
        finally
        {
            System.IO.File.SetUnixFileMode(gameDir, readableMode);
        }
    }
}
