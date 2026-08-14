using LamSims.Core.Catalogs;
using LamSims.Core.Installing;

namespace LamSims.Core.Tests;

public class ZipInstallerTests
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static PackEntry Pack(long? installedSize = null) => new(
        "EP01", "The Sims 4 Get to Work", PackType.Expansion, 1024, installedSize, Digest,
        new[] { new Uri("https://host-a.example.invalid/EP01.zip") }, new[] { "EP01" });

    [Fact]
    public async Task Writes_every_entry_into_the_game_directory()
    {
        using var temp = new TempDir();
        var game = Path.Combine(temp.Path, "game");
        var archive = ZipBuilder.Create(temp.File("EP01.zip"),
            ("EP01/ClientFullBuild0.package", "one"),
            ("EP01/Strings_ENG_US.package", "two"),
            ("Delta/EP01/patch.bin", "three"));

        var result = await new ZipInstaller().InstallAsync(
            Pack(), archive, game, null, CancellationToken.None);

        Assert.Equal(InstallOutcome.Installed, result.Outcome);
        Assert.Equal(3, result.EntriesWritten);
        Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(game, "EP01", "ClientFullBuild0.package")));
        Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(game, "EP01", "Strings_ENG_US.package")));
        Assert.Equal("three", await File.ReadAllTextAsync(Path.Combine(game, "Delta", "EP01", "patch.bin")));
    }

    [Fact]
    public async Task Deletes_the_archive_only_after_a_successful_install()
    {
        using var temp = new TempDir();
        var archive = ZipBuilder.Create(temp.File("EP01.zip"), ("EP01/a.package", "one"));

        await new ZipInstaller().InstallAsync(
            Pack(), archive, Path.Combine(temp.Path, "game"), null, CancellationToken.None);

        Assert.False(File.Exists(archive));
    }

    [Fact]
    public async Task Overwrites_an_existing_file()
    {
        using var temp = new TempDir();
        var game = Path.Combine(temp.Path, "game");
        Directory.CreateDirectory(Path.Combine(game, "EP01"));
        await File.WriteAllTextAsync(Path.Combine(game, "EP01", "a.package"), "stale content that is longer");

        var archive = ZipBuilder.Create(temp.File("EP01.zip"), ("EP01/a.package", "new"));

        var result = await new ZipInstaller().InstallAsync(Pack(), archive, game, null, CancellationToken.None);

        Assert.Equal(InstallOutcome.Installed, result.Outcome);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(game, "EP01", "a.package")));
    }

    [Fact]
    public async Task Rejects_a_zip_slip_entry_before_writing_anything()
    {
        using var temp = new TempDir();
        var game = Path.Combine(temp.Path, "game");
        var archive = ZipBuilder.Create(temp.File("EP01.zip"),
            ("EP01/a.package", "one"),
            ("../escaped.package", "hostile"));

        var result = await new ZipInstaller().InstallAsync(Pack(), archive, game, null, CancellationToken.None);

        Assert.Equal(InstallOutcome.Failed, result.Outcome);
        Assert.Contains("../escaped.package", result.Error);
        Assert.False(File.Exists(Path.Combine(temp.Path, "escaped.package")));

        // Resolution happens up front: the benign entry preceding the hostile one is not
        // written either, so no file lands outside the game directory before the refusal.
        Assert.False(File.Exists(Path.Combine(game, "EP01", "a.package")));
        Assert.True(File.Exists(archive));
    }

    [Fact]
    public async Task Rejects_an_absolute_entry_name()
    {
        using var temp = new TempDir();
        var archive = ZipBuilder.Create(temp.File("EP01.zip"), ("/absolute.package", "hostile"));

        var result = await new ZipInstaller().InstallAsync(
            Pack(), archive, Path.Combine(temp.Path, "game"), null, CancellationToken.None);

        Assert.Equal(InstallOutcome.Failed, result.Outcome);
        Assert.Contains("/absolute.package", result.Error);
    }

    [Fact]
    public async Task Refuses_to_start_without_room_in_the_game_directory()
    {
        using var temp = new TempDir();
        var archive = ZipBuilder.Create(temp.File("EP01.zip"), ("EP01/a.package", "one"));
        var game = Path.Combine(temp.Path, "game");

        var result = await new ZipInstaller().InstallAsync(
            Pack(installedSize: long.MaxValue), archive, game, null, CancellationToken.None);

        Assert.Equal(InstallOutcome.InsufficientSpace, result.Outcome);
        Assert.False(File.Exists(Path.Combine(game, "EP01", "a.package")));
        Assert.True(File.Exists(archive));
    }

    [Fact]
    public async Task Reports_progress_that_ends_at_the_total()
    {
        using var temp = new TempDir();
        var archive = ZipBuilder.Create(temp.File("EP01.zip"),
            ("EP01/a.package", "aaaa"),
            ("EP01/b.package", "bbbbbb"));

        var reports = new List<InstallProgress>();
        var progress = new SyncProgress<InstallProgress>(reports.Add);

        await new ZipInstaller().InstallAsync(
            Pack(), archive, Path.Combine(temp.Path, "game"), progress, CancellationToken.None);

        Assert.Equal(2, reports.Count);
        Assert.Equal("EP01", reports[^1].Code);
        Assert.Equal(10, reports[^1].TotalBytes);
        Assert.Equal(10, reports[^1].BytesWritten);
        Assert.Equal("EP01/b.package", reports[^1].CurrentEntry);
    }

    [Fact]
    public async Task Creates_directory_entries()
    {
        using var temp = new TempDir();
        var game = Path.Combine(temp.Path, "game");
        var archive = ZipBuilder.Create(temp.File("EP01.zip"), ("EP01/empty/", ""));

        var result = await new ZipInstaller().InstallAsync(Pack(), archive, game, null, CancellationToken.None);

        Assert.Equal(InstallOutcome.Installed, result.Outcome);
        Assert.True(Directory.Exists(Path.Combine(game, "EP01", "empty")));
    }

    [Fact]
    public async Task Reports_a_cancelled_install_and_keeps_the_archive()
    {
        using var temp = new TempDir();
        var archive = ZipBuilder.Create(temp.File("EP01.zip"),
            ("EP01/a.package", "one"),
            ("EP01/b.package", "two"));

        using var cancellation = new CancellationTokenSource();
        var progress = new SyncProgress<InstallProgress>(_ => cancellation.Cancel());

        var result = await new ZipInstaller().InstallAsync(
            Pack(), archive, Path.Combine(temp.Path, "game"), progress, cancellation.Token);

        Assert.Equal(InstallOutcome.Cancelled, result.Outcome);
        Assert.Equal(1, result.EntriesWritten);

        // The archive is verified and expensive to replace; a cancelled install must not
        // force the user to download it again.
        Assert.True(File.Exists(archive));
    }

    [Fact]
    public async Task Reports_a_corrupt_archive_rather_than_throwing()
    {
        using var temp = new TempDir();
        var archive = temp.File("EP01.zip");
        await File.WriteAllTextAsync(archive, "this is not a zip file");

        var result = await new ZipInstaller().InstallAsync(
            Pack(), archive, Path.Combine(temp.Path, "game"), null, CancellationToken.None);

        Assert.Equal(InstallOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Error);
        Assert.True(File.Exists(archive));
    }

    [Fact]
    public async Task Reports_a_missing_archive_rather_than_throwing()
    {
        using var temp = new TempDir();

        var result = await new ZipInstaller().InstallAsync(
            Pack(), temp.File("absent.zip"), Path.Combine(temp.Path, "game"), null, CancellationToken.None);

        Assert.Equal(InstallOutcome.Failed, result.Outcome);
        Assert.Contains("absent.zip", result.Error);
    }

    [Fact]
    public async Task Names_the_entry_that_could_not_be_written()
    {
        using var temp = new TempDir();
        var game = Path.Combine(temp.Path, "game");
        Directory.CreateDirectory(Path.Combine(game, "EP01", "a.package"));
        var archive = ZipBuilder.Create(temp.File("EP01.zip"), ("EP01/a.package", "one"));

        var result = await new ZipInstaller().InstallAsync(Pack(), archive, game, null, CancellationToken.None);

        Assert.Equal(InstallOutcome.Failed, result.Outcome);
        Assert.Contains("EP01/a.package", result.Error);
    }

    [Fact]
    public async Task Names_the_entry_whose_parent_directory_cannot_be_created()
    {
        using var temp = new TempDir();
        var game = Path.Combine(temp.Path, "game");
        var archive = ZipBuilder.Create(temp.File("EP01.zip"),
            ("EP01/a", "one"),
            ("EP01/a/b.package", "two"));

        var result = await new ZipInstaller().InstallAsync(Pack(), archive, game, null, CancellationToken.None);

        Assert.Equal(InstallOutcome.Failed, result.Outcome);
        Assert.Contains("EP01/a/b.package", result.Error);
    }

    [Fact]
    public async Task Reports_an_entry_whose_name_is_not_a_valid_path_rather_than_throwing()
    {
        using var temp = new TempDir();
        var archive = ZipBuilder.Create(temp.File("EP01.zip"), ("EP01/a\0b.package", "hostile"));

        var result = await new ZipInstaller().InstallAsync(
            Pack(), archive, Path.Combine(temp.Path, "game"), null, CancellationToken.None);

        Assert.Equal(InstallOutcome.Failed, result.Outcome);
    }
}
