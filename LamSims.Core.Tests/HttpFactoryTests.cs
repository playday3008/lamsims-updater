using System.Net;
using LamSims.Core.Downloading;

namespace LamSims.Core.Tests;

public class HttpFactoryTests
{
    [Fact]
    public void Allows_multiple_http2_connections()
    {
        // Without this, an HTTP/2 host multiplexes every range request over one TCP
        // connection, reproducing exactly the single-connection bottleneck being fixed.
        using var handler = HttpFactory.CreateHandler(8);

        Assert.True(handler.EnableMultipleHttp2Connections);
    }

    [Fact]
    public void Sets_the_connection_ceiling_from_the_argument()
    {
        using var handler = HttpFactory.CreateHandler(12);

        Assert.Equal(12, handler.MaxConnectionsPerServer);
    }

    [Fact]
    public void Disables_automatic_decompression()
    {
        // Transparent decompression would break byte-range accounting.
        using var handler = HttpFactory.CreateHandler(8);

        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
    }

    [Fact]
    public void Bounds_pooled_connection_lifetime()
    {
        using var handler = HttpFactory.CreateHandler(8);

        Assert.InRange(handler.PooledConnectionLifetime, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Client_has_no_whole_operation_timeout()
    {
        // A multi-gigabyte transfer must not race a global clock. Stalls are caught instead by
        // RetryOptions.ReadTimeout inside ChunkFetcher and SingleStreamDownloader.
        using var client = HttpFactory.Create(8);

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [Fact]
    public async Task A_client_built_from_options_honours_their_connection_count()
    {
        // The ceiling has to come from DownloadOptions, or workers queue in the connection
        // pool and the download runs at a fraction of its configured parallelism. Asserting on
        // the handler would only restate the argument, so this drives real requests and counts
        // how many the server ever serves at once.
        var payload = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        await using var server = await TestFileServer.StartAsync(
            payload, new TestFileServerOptions { StallBeforeHeaders = TimeSpan.FromMilliseconds(400) });
        using var client = HttpFactory.Create(new DownloadOptions { Connections = 2 });

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.GetByteArrayAsync(server.FileUrl)));

        Assert.Equal(6, server.RequestCount);
        Assert.InRange(server.PeakConcurrentRequests, 1, 2);
    }
}
