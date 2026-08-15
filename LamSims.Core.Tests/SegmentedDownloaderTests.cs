using System.Security.Cryptography;
using LamSims.Core.Downloading;

namespace LamSims.Core.Tests;

public class SegmentedDownloaderTests
{
    private static byte[] Payload(int size)
    {
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    private static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static SegmentedDownloader Downloader(HttpClient client, DownloadPaths paths, int connections, long chunkSize) =>
        new(client, paths, new DownloadOptions { Connections = connections, ChunkSize = chunkSize },
            RetryOptions.Default, new FakeDelayProvider());

    [Fact]
    public async Task Downloads_and_verifies_a_multi_chunk_archive()
    {
        var content = Payload(1_000_000);
        await using var server = await TestFileServer.StartAsync(content);
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        using var client = HttpFactory.Create(8);

        var result = await Downloader(client, paths, connections: 4, chunkSize: 64 * 1024).DownloadAsync(
            new DownloadRequest("EP01", new[] { server.FileUrl }, content.LongLength, Sha256Of(content)),
            progress: null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(content, await File.ReadAllBytesAsync(paths.ArchiveFile("EP01")));
        Assert.False(File.Exists(paths.PartFile("EP01")));
        Assert.False(File.Exists(paths.StateFile("EP01")));
        Assert.False(result.UsedSingleStream);
    }

    [Fact]
    public async Task Spreads_work_across_mirrors()
    {
        var content = Payload(500_000);
        await using var a = await TestFileServer.StartAsync(content);
        await using var b = await TestFileServer.StartAsync(content);
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        using var client = HttpFactory.Create(8);

        var result = await Downloader(client, paths, connections: 4, chunkSize: 32 * 1024).DownloadAsync(
            new DownloadRequest("EP01", new[] { a.FileUrl, b.FileUrl }, content.LongLength, Sha256Of(content)),
            progress: null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.True(a.RequestCount > 1);
        Assert.True(b.RequestCount > 1);
    }

    [Fact]
    public async Task Fails_fast_when_the_server_size_disagrees_with_the_request()
    {
        var content = Payload(100_000);
        await using var server = await TestFileServer.StartAsync(content);
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        using var client = HttpFactory.Create(8);

        var result = await Downloader(client, paths, connections: 4, chunkSize: 16 * 1024).DownloadAsync(
            new DownloadRequest("EP01", new[] { server.FileUrl }, 999_999, Sha256Of(content)),
            progress: null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.Contains("999999", result.Error!.Replace(",", "").Replace(" ", ""));
        // Only the probe was issued, so no gigabytes went into a doomed download.
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task Quarantines_an_archive_whose_digest_does_not_match()
    {
        var content = Payload(100_000);
        await using var server = await TestFileServer.StartAsync(content);
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        using var client = HttpFactory.Create(8);
        const string wrong = "0000000000000000000000000000000000000000000000000000000000000000";

        var result = await Downloader(client, paths, connections: 4, chunkSize: 16 * 1024).DownloadAsync(
            new DownloadRequest("EP01", new[] { server.FileUrl }, content.LongLength, wrong),
            progress: null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.ChecksumMismatch, result.Outcome);
        Assert.Equal(Sha256Of(content), result.ActualSha256);
        Assert.True(File.Exists(paths.QuarantineFile("EP01")));
    }

    [Fact]
    public async Task Resume_refetches_only_the_missing_chunks()
    {
        var content = Payload(400_000);
        await using var server = await TestFileServer.StartAsync(content);
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();
        using var client = HttpFactory.Create(8);

        const long chunkSize = 100_000;
        var request = new DownloadRequest("EP01", new[] { server.FileUrl }, content.LongLength, Sha256Of(content));

        // Simulate a kill after two of four chunks: preallocate, write those chunks, and
        // record them in the sidecar with the server's current validator.
        using (var fs = new FileStream(paths.PartFile("EP01"), FileMode.Create, FileAccess.Write))
        {
            fs.SetLength(content.LongLength);
            await fs.WriteAsync(content.AsMemory(0, 200_000));
        }
        var probe = await new RangeProbe(client).ProbeAsync(server.FileUrl, CancellationToken.None);
        await new PartStateStore(paths.StateFile("EP01")).SaveAsync(
            new PartState("EP01", content.LongLength, Sha256Of(content), chunkSize,
                new[]
                {
                    new CompletedChunk(0, server.FileUrl.ToString()),
                    new CompletedChunk(1, server.FileUrl.ToString()),
                },
                new[] { probe.Validator }),
            CancellationToken.None);

        server.ResetCounters();
        var result = await Downloader(client, paths, connections: 2, chunkSize).DownloadAsync(
            request, progress: null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(content, await File.ReadAllBytesAsync(paths.ArchiveFile("EP01")));
        // One probe plus exactly the two missing chunks.
        Assert.Equal(3, server.RequestCount);
        Assert.Contains("bytes=200000-299999", server.ReceivedRangeHeaders);
        Assert.Contains("bytes=300000-399999", server.ReceivedRangeHeaders);
        Assert.DoesNotContain("bytes=0-99999", server.ReceivedRangeHeaders);
    }

    [Fact]
    public async Task A_changed_validator_discards_that_mirrors_progress()
    {
        var content = Payload(400_000);
        var options = new TestFileServerOptions { ETag = "\"v1\"" };
        await using var server = await TestFileServer.StartAsync(content, options);
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();
        using var client = HttpFactory.Create(8);

        using (var fs = new FileStream(paths.PartFile("EP01"), FileMode.Create, FileAccess.Write))
            fs.SetLength(content.LongLength);
        await new PartStateStore(paths.StateFile("EP01")).SaveAsync(
            new PartState("EP01", content.LongLength, Sha256Of(content), 100_000,
                new[]
                {
                    new CompletedChunk(0, server.FileUrl.ToString()),
                    new CompletedChunk(1, server.FileUrl.ToString()),
                },
                new[] { new MirrorValidator(server.FileUrl.ToString(), "\"v1\"", null, content.LongLength) }),
            CancellationToken.None);

        // The entity changes before the resume.
        options.ETag = "\"v2\"";
        server.ResetCounters();

        var result = await Downloader(client, paths, connections: 2, chunkSize: 100_000).DownloadAsync(
            new DownloadRequest("EP01", new[] { server.FileUrl }, content.LongLength, Sha256Of(content)),
            progress: null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(content, await File.ReadAllBytesAsync(paths.ArchiveFile("EP01")));
        // All four chunks were refetched, not just the two that were missing.
        Assert.Equal(5, server.RequestCount);
    }

    [Fact]
    public async Task Only_the_changed_mirrors_chunks_are_refetched()
    {
        var content = Payload(400_000);
        var staleOptions = new TestFileServerOptions { ETag = "\"v1\"" };
        await using var stale = await TestFileServer.StartAsync(content, staleOptions);
        await using var steady = await TestFileServer.StartAsync(
            content, new TestFileServerOptions { ETag = "\"steady\"" });
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();
        using var client = HttpFactory.Create(8);

        // All four chunks are on disk: two served by each mirror.
        using (var fs = new FileStream(paths.PartFile("EP01"), FileMode.Create, FileAccess.Write))
        {
            fs.SetLength(content.LongLength);
            await fs.WriteAsync(content);
        }
        await new PartStateStore(paths.StateFile("EP01")).SaveAsync(
            new PartState("EP01", content.LongLength, Sha256Of(content), 100_000,
                new[]
                {
                    new CompletedChunk(0, stale.FileUrl.ToString()),
                    new CompletedChunk(1, stale.FileUrl.ToString()),
                    new CompletedChunk(2, steady.FileUrl.ToString()),
                    new CompletedChunk(3, steady.FileUrl.ToString()),
                },
                new[]
                {
                    new MirrorValidator(stale.FileUrl.ToString(), "\"v1\"", null, content.LongLength),
                    new MirrorValidator(steady.FileUrl.ToString(), "\"steady\"", null, content.LongLength),
                }),
            CancellationToken.None);

        // Only the first mirror's entity changes.
        staleOptions.ETag = "\"v2\"";
        stale.ResetCounters();
        steady.ResetCounters();

        var result = await Downloader(client, paths, connections: 2, chunkSize: 100_000).DownloadAsync(
            new DownloadRequest("EP01", new[] { stale.FileUrl, steady.FileUrl },
                content.LongLength, Sha256Of(content)),
            progress: null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(content, await File.ReadAllBytesAsync(paths.ArchiveFile("EP01")));

        // Exactly the two chunks attributed to the changed mirror came back; the two
        // attributed to the steady mirror were kept.
        var ranges = stale.ReceivedRangeHeaders.Concat(steady.ReceivedRangeHeaders)
            .Where(r => r is not null && r != "bytes=0-0").ToArray();
        Assert.Equal(2, ranges.Length);
        Assert.Contains("bytes=0-99999", ranges);
        Assert.Contains("bytes=100000-199999", ranges);
        Assert.DoesNotContain("bytes=200000-299999", ranges);
        Assert.DoesNotContain("bytes=300000-399999", ranges);
    }

    [Fact]
    public async Task A_mirror_whose_entity_changes_mid_download_is_set_aside_for_another()
    {
        // A round-robin cluster with per-node ETags answers an If-Range with 200 for identical
        // bytes. Failing the whole download over that makes the pack permanently unfetchable
        // even with a healthy mirror listed, and the probe re-derives the same unstable
        // validator on every run, so it never self-heals.
        var content = Payload(400_000);
        var changingOptions = new TestFileServerOptions { ETag = "\"v1\"" };
        await using var changing = await TestFileServer.StartAsync(content, changingOptions);
        await using var steady = await TestFileServer.StartAsync(
            content, new TestFileServerOptions { ETag = "\"steady\"" });
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        using var client = HttpFactory.Create(8);

        // One connection, so the chunks are pulled in order and the entity changes exactly
        // once, after the first of four has landed.
        var progress = new SyncProgress<DownloadProgress>(_ => changingOptions.ETag = "\"v2\"");

        var result = await Downloader(client, paths, connections: 1, chunkSize: 100_000).DownloadAsync(
            new DownloadRequest("EP01", new[] { changing.FileUrl, steady.FileUrl },
                content.LongLength, Sha256Of(content)),
            progress, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(content, await File.ReadAllBytesAsync(paths.ArchiveFile("EP01")));

        // The remaining chunks came from the mirror that did not change.
        Assert.True(steady.RequestCount >= 2, $"steady mirror served {steady.RequestCount} requests");
    }

    [Fact]
    public async Task A_validator_change_with_no_mirror_left_is_reported_rather_than_thrown()
    {
        var content = Payload(400_000);
        var options = new TestFileServerOptions { ETag = "\"v1\"" };
        await using var server = await TestFileServer.StartAsync(content, options);
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        using var client = HttpFactory.Create(8);

        var progress = new SyncProgress<DownloadProgress>(_ => options.ETag = "\"v2\"");

        var result = await Downloader(client, paths, connections: 1, chunkSize: 100_000).DownloadAsync(
            new DownloadRequest("EP01", new[] { server.FileUrl }, content.LongLength, Sha256Of(content)),
            progress, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.Contains("changed on the server", result.Error!);
    }

    [Fact]
    public async Task A_mirror_reporting_the_wrong_size_does_not_stop_the_others_being_probed()
    {
        var content = Payload(400_000);
        await using var stale = await TestFileServer.StartAsync(Payload(123_456));
        await using var current = await TestFileServer.StartAsync(content);
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        using var client = HttpFactory.Create(8);

        var result = await Downloader(client, paths, connections: 2, chunkSize: 100_000).DownloadAsync(
            new DownloadRequest("EP01", new[] { stale.FileUrl, current.FileUrl },
                content.LongLength, Sha256Of(content)),
            progress: null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(content, await File.ReadAllBytesAsync(paths.ArchiveFile("EP01")));
    }

    [Fact]
    public async Task A_torn_sidecar_restarts_the_download_instead_of_throwing()
    {
        var content = Payload(200_000);
        await using var server = await TestFileServer.StartAsync(content);
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.StateFile("EP01"), "{\"Code\":\"EP0");
        using var client = HttpFactory.Create(8);

        var result = await Downloader(client, paths, connections: 2, chunkSize: 100_000).DownloadAsync(
            new DownloadRequest("EP01", new[] { server.FileUrl }, content.LongLength, Sha256Of(content)),
            progress: null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
    }

    [Fact]
    public async Task A_sidecar_whose_collections_are_absent_restarts_the_download()
    {
        // PartState is a positional record, so a JSON document that omits CompletedChunks and
        // Mirrors, or sets them to null, deserializes with both left null. The part file is
        // exactly the archive's length, the precondition for trusting a sidecar at all.
        var content = Payload(200_000);
        await using var server = await TestFileServer.StartAsync(content);
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();
        using var client = HttpFactory.Create(8);

        const long chunkSize = 100_000;
        using (var fs = new FileStream(paths.PartFile("EP01"), FileMode.Create, FileAccess.Write))
            fs.SetLength(content.LongLength);

        await File.WriteAllTextAsync(
            paths.StateFile("EP01"),
            $$"""
              {"Code":"EP01","TotalSize":{{content.LongLength}},
               "ExpectedSha256":"{{Sha256Of(content)}}","ChunkSize":{{chunkSize}},
               "CompletedChunks":null,"Mirrors":null}
              """);

        var result = await Downloader(client, paths, connections: 2, chunkSize).DownloadAsync(
            new DownloadRequest("EP01", new[] { server.FileUrl }, content.LongLength, Sha256Of(content)),
            progress: null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(content, await File.ReadAllBytesAsync(paths.ArchiveFile("EP01")));
    }

    [Theory]
    [InlineData("\"CompletedChunks\":[null],\"Mirrors\":[]")]
    [InlineData("\"CompletedChunks\":[],\"Mirrors\":[null]")]
    public async Task A_sidecar_holding_a_null_entry_restarts_the_download(string collections)
    {
        // One step past the absent-collection case: the list is there, an element inside it is
        // not. No serializer writes this, but the sidecar is a file on disk that anything may
        // have written, and the projections over it would dereference the null.
        var content = Payload(200_000);
        await using var server = await TestFileServer.StartAsync(content);
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();
        using var client = HttpFactory.Create(8);

        const long chunkSize = 100_000;
        using (var fs = new FileStream(paths.PartFile("EP01"), FileMode.Create, FileAccess.Write))
            fs.SetLength(content.LongLength);

        await File.WriteAllTextAsync(
            paths.StateFile("EP01"),
            $$"""
              {"Code":"EP01","TotalSize":{{content.LongLength}},
               "ExpectedSha256":"{{Sha256Of(content)}}","ChunkSize":{{chunkSize}},
               {{collections}}}
              """);

        var result = await Downloader(client, paths, connections: 2, chunkSize).DownloadAsync(
            new DownloadRequest("EP01", new[] { server.FileUrl }, content.LongLength, Sha256Of(content)),
            progress: null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(content, await File.ReadAllBytesAsync(paths.ArchiveFile("EP01")));
    }

    [Fact]
    public async Task A_sidecar_with_incoherent_chunk_indices_restarts_the_download()
    {
        // The sidecar is a file on disk; anything may have written it. A repeated index, or one
        // outside the plan, is nobody's valid state, and reading it as a dictionary keyed by
        // index would throw straight out of DownloadAsync.
        var content = Payload(200_000);
        await using var server = await TestFileServer.StartAsync(content);
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();
        using var client = HttpFactory.Create(8);

        const long chunkSize = 100_000;
        using (var fs = new FileStream(paths.PartFile("EP01"), FileMode.Create, FileAccess.Write))
            fs.SetLength(content.LongLength);

        var probe = await new RangeProbe(client).ProbeAsync(server.FileUrl, CancellationToken.None);
        await new PartStateStore(paths.StateFile("EP01")).SaveAsync(
            new PartState("EP01", content.LongLength, Sha256Of(content), chunkSize,
                new[]
                {
                    new CompletedChunk(0, server.FileUrl.ToString()),
                    new CompletedChunk(0, server.FileUrl.ToString()),
                    new CompletedChunk(7, server.FileUrl.ToString()),
                },
                new[] { probe.Validator }),
            CancellationToken.None);

        var result = await Downloader(client, paths, connections: 2, chunkSize).DownloadAsync(
            new DownloadRequest("EP01", new[] { server.FileUrl }, content.LongLength, Sha256Of(content)),
            progress: null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(content, await File.ReadAllBytesAsync(paths.ArchiveFile("EP01")));
    }

    [Fact]
    public async Task A_part_file_of_the_wrong_length_discards_the_sidecar()
    {
        // The sidecar claims chunk 0 is complete, but the partial file is shorter than the
        // archive. Preallocation would extend it with zeros and the download would skip those
        // "complete" bytes, so the archive would fail its checksum with nothing to show why.
        var content = Payload(200_000);
        await using var server = await TestFileServer.StartAsync(content);
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();
        using var client = HttpFactory.Create(8);

        const long chunkSize = 100_000;
        await File.WriteAllBytesAsync(paths.PartFile("EP01"), content[..50_000]);

        var probe = await new RangeProbe(client).ProbeAsync(server.FileUrl, CancellationToken.None);
        await new PartStateStore(paths.StateFile("EP01")).SaveAsync(
            new PartState("EP01", content.LongLength, Sha256Of(content), chunkSize,
                new[] { new CompletedChunk(0, server.FileUrl.ToString()) },
                new[] { probe.Validator }),
            CancellationToken.None);

        server.ResetCounters();
        var result = await Downloader(client, paths, connections: 2, chunkSize).DownloadAsync(
            new DownloadRequest("EP01", new[] { server.FileUrl }, content.LongLength, Sha256Of(content)),
            progress: null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(content, await File.ReadAllBytesAsync(paths.ArchiveFile("EP01")));
        // Chunk 0 was refetched rather than trusted: one probe plus both chunks.
        Assert.Contains("bytes=0-99999", server.ReceivedRangeHeaders);
        Assert.Equal(3, server.RequestCount);
    }

    [Fact]
    public async Task Cancellation_keeps_the_partial_file_and_its_sidecar()
    {
        var content = Payload(2_000_000);
        await using var server = await TestFileServer.StartAsync(content);
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        using var client = HttpFactory.Create(8);
        using var cts = new CancellationTokenSource();

        var progress = new SyncProgress<DownloadProgress>(_ => cts.Cancel());

        var result = await Downloader(client, paths, connections: 2, chunkSize: 32 * 1024)
            .DownloadAsync(
                new DownloadRequest("EP01", new[] { server.FileUrl }, content.LongLength, Sha256Of(content)),
                progress, cts.Token);

        Assert.Equal(DownloadOutcome.Cancelled, result.Outcome);
        Assert.True(File.Exists(paths.PartFile("EP01")));
        Assert.True(File.Exists(paths.StateFile("EP01")));
        Assert.False(File.Exists(paths.ArchiveFile("EP01")));
    }

    [Fact]
    public async Task Reports_monotonic_progress_up_to_the_total()
    {
        var content = Payload(500_000);
        await using var server = await TestFileServer.StartAsync(content);
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        using var client = HttpFactory.Create(8);

        var reports = new List<DownloadProgress>();
        var progress = new SyncProgress<DownloadProgress>(p => { lock (reports) reports.Add(p); });

        await Downloader(client, paths, connections: 4, chunkSize: 50_000).DownloadAsync(
            new DownloadRequest("EP01", new[] { server.FileUrl }, content.LongLength, Sha256Of(content)),
            progress, CancellationToken.None);

        List<DownloadProgress> snapshot;
        lock (reports) snapshot = reports.ToList();

        Assert.NotEmpty(snapshot);
        Assert.All(snapshot, p => Assert.Equal(500_000, p.TotalBytes));
        Assert.All(snapshot, p => Assert.InRange(p.BytesCompleted, 0, 500_000));

        // Only one worker reports at a time, and always the newest snapshot it can see, so
        // reports arrive in non-decreasing order however the workers interleave.
        for (var i = 1; i < snapshot.Count; i++)
            Assert.True(snapshot[i].BytesCompleted >= snapshot[i - 1].BytesCompleted,
                $"report {i} went backwards: {snapshot[i - 1].BytesCompleted} -> {snapshot[i].BytesCompleted}");

        Assert.Equal(500_000, snapshot[^1].BytesCompleted);
    }

    [Fact]
    public async Task A_non_writable_download_root_yields_Failed_not_a_thrown_exception()
    {
        // Unix permission bits are what this test exercises; they do not apply on Windows.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var content = Payload(1000);
        await using var server = await TestFileServer.StartAsync(content);
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        paths.EnsureCreated();

        // Read+execute only: the probe and any reads still work, but nothing can be created or
        // written inside Root, which covers Directory.CreateDirectory, File.OpenHandle,
        // File.Move and Sha256Verifier's FileStream open.
        File.SetUnixFileMode(paths.Root, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            using var client = HttpFactory.Create(4);

            var result = await Downloader(client, paths, connections: 2, chunkSize: 500).DownloadAsync(
                new DownloadRequest("EP01", new[] { server.FileUrl }, content.LongLength, Sha256Of(content)),
                progress: null, CancellationToken.None);

            if (result.Outcome == DownloadOutcome.Completed)
            {
                // Root, or a capability that bypasses DAC such as CAP_DAC_OVERRIDE, ignores Unix
                // permission bits, so the download succeeds and there is nothing to assert.
                // Checking the effective user name would miss non-root processes holding the
                // same capability, so the outcome is the signal and the test bows out here.
                return;
            }

            Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        }
        finally
        {
            // Restore write access so TempDir's recursive delete on dispose can succeed.
            File.SetUnixFileMode(paths.Root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task A_download_root_that_cannot_be_created_yields_Failed_not_a_thrown_exception()
    {
        // The test above starts from a Root that exists; here it cannot be created at all, so
        // EnsureCreated itself throws and must be inside the try.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var content = Payload(1000);
        await using var server = await TestFileServer.StartAsync(content);
        using var temp = new TempDir();
        using var locked = new ReadOnlyDir(temp.Path);
        using var client = HttpFactory.Create(4);

        var result = await Downloader(client, new DownloadPaths(locked.Child), connections: 2, chunkSize: 500)
            .DownloadAsync(
                new DownloadRequest("EP01", new[] { server.FileUrl }, content.LongLength, Sha256Of(content)),
                progress: null, CancellationToken.None);

        // A process holding CAP_DAC_OVERRIDE ignores the permission bits and succeeds; there is
        // nothing to assert in that case.
        if (result.Outcome == DownloadOutcome.Completed) return;

        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task A_blank_download_directory_is_reported_rather_than_thrown()
    {
        // AppSettings.DownloadDirectory is free text, and a blank one reaches
        // Directory.CreateDirectory(""), an ArgumentException outside every filter here.
        var content = Payload(1000);
        await using var server = await TestFileServer.StartAsync(content);
        using var client = HttpFactory.Create(4);

        var result = await Downloader(client, new DownloadPaths("  "), connections: 2, chunkSize: 500)
            .DownloadAsync(
                new DownloadRequest("EP01", new[] { server.FileUrl }, content.LongLength, Sha256Of(content)),
                progress: null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.Contains("download directory", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task A_pack_code_that_is_not_a_file_name_is_reported_rather_than_thrown()
    {
        var content = Payload(1000);
        await using var server = await TestFileServer.StartAsync(content);
        using var temp = new TempDir();
        using var client = HttpFactory.Create(4);

        var result = await Downloader(client, new DownloadPaths(temp.Path), connections: 2, chunkSize: 500)
            .DownloadAsync(
                new DownloadRequest("../EP01", new[] { server.FileUrl }, content.LongLength, Sha256Of(content)),
                progress: null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.Contains("EP01", result.Error!);
    }

    [Fact]
    public async Task Refuses_to_start_without_room_on_disk()
    {
        var content = Payload(1000);
        await using var server = await TestFileServer.StartAsync(content);
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        using var client = HttpFactory.Create(8);

        var result = await Downloader(client, paths, connections: 2, chunkSize: 500).DownloadAsync(
            new DownloadRequest("EP01", new[] { server.FileUrl }, long.MaxValue, Sha256Of(content)),
            progress: null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.Contains("free", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task Fails_the_download_when_a_chunk_exhausts_every_retry()
    {
        // Every ranged response is cut short, so no chunk can complete. A mirror that accepts the
        // connection and then stops sending is the ordinary way a real download dies.
        var content = Payload(2_000_000);
        await using var server = await TestFileServer.StartAsync(
            content, new TestFileServerOptions { DropAfterBytes = 1024 });
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);
        using var client = HttpFactory.Create(8);

        var result = await Downloader(client, paths, connections: 4, chunkSize: 64 * 1024).DownloadAsync(
            new DownloadRequest("EP01", new[] { server.FileUrl }, content.LongLength, Sha256Of(content)),
            progress: null, CancellationToken.None);

        // Failed, not ChecksumMismatch: a transport failure reported as a checksum failure sends
        // the user hunting a corrupt mirror, and leaves a .zip.bad they have to clear by hand.
        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(paths.QuarantineFile("EP01")));
        Assert.False(File.Exists(paths.ArchiveFile("EP01")));
    }
}
