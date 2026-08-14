using System.Security.Cryptography;
using LamSims.Core;
using LamSims.Core.Catalogs;
using LamSims.Core.Downloading;
using LamSims.Core.Installing;

namespace LamSims.Core.Tests;

public class PackWorkflowTests
{
    private static string Sha256Of(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static PackEntry Pack(byte[] archive, Uri url) => new(
        "EP01", "The Sims 4 Get to Work", PackType.Expansion, archive.LongLength, null,
        Sha256Of(archive), new[] { url }, new[] { "EP01" });

    private static byte[] BuildArchive(TempDir temp)
    {
        var path = ZipBuilder.Create(temp.File("source.zip"), ("EP01/a.package", "content"));
        return File.ReadAllBytes(path);
    }

    private static PackWorkflow Workflow(TempDir temp, HttpClient client, out DownloadPaths paths)
    {
        paths = new DownloadPaths(Path.Combine(temp.Path, "downloads"));
        var options = new DownloadOptions { Connections = 2 };

        return new PackWorkflow(
            new SegmentedDownloader(client, paths, options, RetryOptions.Default, new FakeDelayProvider()),
            new ZipInstaller(),
            paths);
    }

    [Fact]
    public async Task Downloads_then_installs_a_pack()
    {
        using var temp = new TempDir();
        var archive = BuildArchive(temp);

        await using var server = await TestFileServer.StartAsync(archive);
        using var client = new HttpClient();

        var workflow = Workflow(temp, client, out var paths);
        var game = Path.Combine(temp.Path, "game");

        var result = await workflow.RunAsync(
            Pack(archive, server.FileUrl), game, null, null, CancellationToken.None);

        Assert.Equal(PackStage.Done, result.ReachedStage);
        Assert.Equal(DownloadOutcome.Completed, result.Download!.Outcome);
        Assert.Equal(InstallOutcome.Installed, result.Install!.Outcome);
        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(game, "EP01", "a.package")));
        Assert.False(File.Exists(paths.ArchiveFile("EP01")));
    }

    [Fact]
    public async Task Stops_at_the_download_when_the_checksum_does_not_match()
    {
        using var temp = new TempDir();
        var archive = BuildArchive(temp);

        await using var server = await TestFileServer.StartAsync(archive);
        using var client = new HttpClient();

        var workflow = Workflow(temp, client, out _);
        var pack = Pack(archive, server.FileUrl) with
        {
            Sha256 = new string('a', 64),
        };

        var result = await workflow.RunAsync(
            pack, Path.Combine(temp.Path, "game"), null, null, CancellationToken.None);

        Assert.Equal(PackStage.Downloading, result.ReachedStage);
        Assert.Equal(DownloadOutcome.ChecksumMismatch, result.Download!.Outcome);
        Assert.Null(result.Install);
    }

    [Fact]
    public async Task Installs_an_archive_that_is_already_verified_without_downloading_again()
    {
        using var temp = new TempDir();
        var archive = BuildArchive(temp);

        await using var server = await TestFileServer.StartAsync(archive);
        using var client = new HttpClient();

        var workflow = Workflow(temp, client, out var paths);
        paths.EnsureCreated();
        await File.WriteAllBytesAsync(paths.ArchiveFile("EP01"), archive);

        var game = Path.Combine(temp.Path, "game");

        var result = await workflow.RunAsync(
            Pack(archive, server.FileUrl), game, null, null, CancellationToken.None);

        Assert.Equal(PackStage.Done, result.ReachedStage);
        Assert.Null(result.Download);

        // The engine writes that name only after a verified digest, so re-hashing gigabytes
        // on every retry would cost more than it protects.
        Assert.Equal(0, server.RequestCount);
        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(game, "EP01", "a.package")));
    }

    [Fact]
    public async Task Downloads_again_when_an_existing_archive_is_the_wrong_length()
    {
        using var temp = new TempDir();
        var archive = BuildArchive(temp);

        await using var server = await TestFileServer.StartAsync(archive);
        using var client = new HttpClient();

        var workflow = Workflow(temp, client, out var paths);
        paths.EnsureCreated();
        await File.WriteAllBytesAsync(paths.ArchiveFile("EP01"), archive[..^1]);

        var result = await workflow.RunAsync(
            Pack(archive, server.FileUrl),
            Path.Combine(temp.Path, "game"), null, null, CancellationToken.None);

        Assert.Equal(PackStage.Done, result.ReachedStage);
        Assert.NotNull(result.Download);
        Assert.True(server.RequestCount > 0);
    }

    [Fact]
    public async Task Stops_at_the_install_and_keeps_the_archive_when_installing_fails()
    {
        using var temp = new TempDir();
        var archive = BuildArchive(temp);

        await using var server = await TestFileServer.StartAsync(archive);
        using var client = new HttpClient();

        var workflow = Workflow(temp, client, out var paths);
        paths.EnsureCreated();
        await File.WriteAllBytesAsync(paths.ArchiveFile("EP01"), archive);

        var pack = Pack(archive, server.FileUrl) with { InstalledSize = long.MaxValue };

        var result = await workflow.RunAsync(
            pack, Path.Combine(temp.Path, "game"), null, null, CancellationToken.None);

        Assert.Equal(PackStage.Installing, result.ReachedStage);
        Assert.Equal(InstallOutcome.InsufficientSpace, result.Install!.Outcome);
        Assert.True(File.Exists(paths.ArchiveFile("EP01")));
    }

    [Fact]
    public async Task Cancelling_during_the_install_phase_reports_it_and_keeps_the_verified_archive()
    {
        using var temp = new TempDir();
        var archive = ZipBuilder.Create(temp.File("source.zip"),
            ("EP01/a.package", "one"),
            ("EP01/b.package", "two"));
        var bytes = File.ReadAllBytes(archive);

        await using var server = await TestFileServer.StartAsync(bytes);
        using var client = new HttpClient();

        var workflow = Workflow(temp, client, out var paths);
        paths.EnsureCreated();
        await File.WriteAllBytesAsync(paths.ArchiveFile("EP01"), bytes);

        using var cancellation = new CancellationTokenSource();
        var installProgress = new SyncProgress<InstallProgress>(_ => cancellation.Cancel());

        var result = await workflow.RunAsync(
            Pack(bytes, server.FileUrl), Path.Combine(temp.Path, "game"),
            null, installProgress, cancellation.Token);

        Assert.Equal(PackStage.Installing, result.ReachedStage);
        Assert.Equal(InstallOutcome.Cancelled, result.Install!.Outcome);

        // The archive is already verified; a cancelled install must not force a re-download.
        Assert.True(File.Exists(paths.ArchiveFile("EP01")));
    }
}
