using System.Security.Cryptography;
using LamSims.Core.Downloading;
using LamSims.Core.Unlocking;

namespace LamSims.Core.Tests;

public class StaticUnlockerAssetSourceTests
{
    private static byte[] Payload() => Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();

    private static string Digest(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static (StaticUnlockerAssetSource Source, DownloadPaths Paths) Build(
        TestFileServer server, TempDir dir, byte[] payload, string? digestOverride = null,
        long? sizeOverride = null, RetryOptions? retry = null)
    {
        var paths = new DownloadPaths(dir.Path);
        var pins = new Dictionary<ClientKind, AssetPin>
        {
            [ClientKind.EaApp] = new(server.FileUrl.ToString(), sizeOverride ?? payload.Length,
                                     digestOverride ?? Digest(payload), "ea_app_version.dll"),
        };
        return (new StaticUnlockerAssetSource(new HttpClient(), paths, pins, retry), paths);
    }

    [Fact]
    public async Task A_matching_digest_returns_the_cached_path()
    {
        using var dir = new TempDir();
        var payload = Payload();
        await using var server = await TestFileServer.StartAsync(payload);
        var (source, paths) = Build(server, dir, payload);

        var path = await source.GetDllAsync(ClientKind.EaApp, CancellationToken.None);

        Assert.Equal(paths.UnlockerAssetFile("ea_app_version.dll"), path);
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
    }

    // A throw alone would pass against an implementation that had already written the bad bytes.
    [Fact]
    public async Task A_mismatched_digest_throws_and_caches_nothing()
    {
        using var dir = new TempDir();
        var payload = Payload();
        await using var server = await TestFileServer.StartAsync(payload);
        var (source, paths) = Build(server, dir, payload, digestOverride: new string('a', 64));

        var ex = await Assert.ThrowsAsync<UnlockerAssetMismatchException>(
            () => source.GetDllAsync(ClientKind.EaApp, CancellationToken.None));

        Assert.Equal(new string('a', 64), ex.Expected);
        Assert.Empty(Directory.GetFiles(paths.Root));
    }

    /// <summary>
    /// The pair: it throws AND nothing is cached. Without a Content-Length the advertised-size
    /// gate cannot fire, so the pinned size is the only thing bounding the write. The payload is
    /// unauthenticated until the digest check, and the volume being written to is the one the
    /// pack queue downloads into.
    /// </summary>
    [Fact]
    public async Task A_response_with_no_content_length_cannot_write_past_the_pinned_size()
    {
        using var dir = new TempDir();
        var payload = Payload();
        await using var server = await TestFileServer.StartAsync(
            payload, new TestFileServerOptions { OmitContentLength = true });

        // The digest still matches the payload, so only the size cap can stop this.
        var (source, paths) = Build(server, dir, payload, sizeOverride: 1024);

        var ex = await Assert.ThrowsAsync<IOException>(
            () => source.GetDllAsync(ClientKind.EaApp, CancellationToken.None));

        Assert.Contains("more than the expected 1024 bytes", ex.Message);
        Assert.Empty(Directory.GetFiles(paths.Root));
    }

    // A re-fetch reproduces the file's mtime and bytes, so only the request count distinguishes
    // a cache hit from no cache at all.
    [Fact]
    public async Task A_cached_file_with_the_right_digest_is_not_fetched_again()
    {
        using var dir = new TempDir();
        var payload = Payload();
        await using var server = await TestFileServer.StartAsync(payload);
        var (source, _) = Build(server, dir, payload);

        await source.GetDllAsync(ClientKind.EaApp, CancellationToken.None);
        var afterFirst = server.RequestCount;

        await source.GetDllAsync(ClientKind.EaApp, CancellationToken.None);

        Assert.Equal(afterFirst, server.RequestCount);
    }

    // A size disagreeing with the pin must fail before transferring, as the pack path does with
    // catalog sizes.
    [Fact]
    public async Task A_size_disagreeing_with_the_pin_fails_without_caching()
    {
        using var dir = new TempDir();
        var payload = Payload();
        await using var server = await TestFileServer.StartAsync(payload);
        var (source, paths) = Build(server, dir, payload, sizeOverride: payload.Length + 1);

        await Assert.ThrowsAnyAsync<Exception>(
            () => source.GetDllAsync(ClientKind.EaApp, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(paths.Root));
    }

    /// <summary>
    /// HttpFactory sets Timeout.InfiniteTimeSpan, so without an explicit header deadline this fetch
    /// inherits no timeout at all and a stalling server hangs the install forever.
    /// </summary>
    [Fact]
    public async Task A_server_that_stalls_before_headers_fails_rather_than_hanging()
    {
        using var dir = new TempDir();
        var payload = Payload();
        await using var server = await TestFileServer.StartAsync(payload, new()
        {
            StallBeforeHeaders = TimeSpan.FromSeconds(30),
        });
        var (source, _) = Build(server, dir, payload,
            retry: RetryOptions.Default with { HeaderTimeout = TimeSpan.FromMilliseconds(200) });

        await Assert.ThrowsAnyAsync<Exception>(
            () => source.GetDllAsync(ClientKind.EaApp, CancellationToken.None));
    }

    /// <summary>
    /// Headers arrive and then the body stops, which is the case CopyToAsync does not bound.
    /// HttpFactory's client is built with Timeout.InfiniteTimeSpan, so without a per-read bound
    /// this hangs.
    /// </summary>
    [Fact]
    public async Task A_server_that_stalls_mid_body_fails_rather_than_hanging()
    {
        using var dir = new TempDir();
        var payload = Payload();
        await using var server = await TestFileServer.StartAsync(payload, new()
        {
            StallAfterBytes = 1024,
            StallFor = TimeSpan.FromSeconds(30),
        });
        var (source, paths) = Build(server, dir, payload,
            retry: RetryOptions.Default with { ReadTimeout = TimeSpan.FromMilliseconds(200) });

        await Assert.ThrowsAnyAsync<Exception>(
            () => source.GetDllAsync(ClientKind.EaApp, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(paths.Root));
    }

    /// <summary>
    /// A cached file whose bytes have changed is not a cache hit. Without the digest check on the
    /// cached path, a corrupted or substituted DLL goes straight to the backend.
    /// </summary>
    [Fact]
    public async Task A_cached_file_whose_digest_no_longer_matches_is_refetched()
    {
        using var dir = new TempDir();
        var payload = Payload();
        await using var server = await TestFileServer.StartAsync(payload);
        var (source, paths) = Build(server, dir, payload);
        paths.EnsureCreated();
        await File.WriteAllBytesAsync(paths.UnlockerAssetFile("ea_app_version.dll"), [0xDE, 0xAD]);

        var path = await source.GetDllAsync(ClientKind.EaApp, CancellationToken.None);

        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        Assert.True(server.RequestCount > 0, "the corrupt cache entry was trusted");
    }

    /// <summary>
    /// The pins the application ships with, asserted as constants. Fetching them would reach
    /// github.com and would fail the day upstream rotates the asset.
    /// </summary>
    [Fact]
    public void The_shipped_pins_cover_both_clients_with_the_measured_digests()
    {
        var pins = StaticUnlockerAssetSource.ShippedPins;

        Assert.Equal(2, pins.Count);
        Assert.Equal("70553a2b4d53eddf1eeb290e346a9b562c71f52d46874bfb708e9d962469a736",
                     pins[ClientKind.EaApp].Sha256);
        Assert.Equal(245248, pins[ClientKind.EaApp].Size);
        Assert.Equal("cf784476719a93e3fb8457a2d4c4580b691b6d04592b9a4467acf563f30d2b83",
                     pins[ClientKind.Origin].Sha256);
        Assert.Equal(192512, pins[ClientKind.Origin].Size);
        Assert.All(pins.Values, p => Assert.StartsWith("https://", p.Url));
    }
}
