using System;
using System.Net.Http;
using System.Threading;
using System.Net;

namespace LamSims.Core.Downloading;

/// <summary>
/// One shared, explicitly configured handler for every request the engine makes.
/// </summary>
public static class HttpFactory
{
    public static SocketsHttpHandler CreateHandler(int maxConnectionsPerServer) => new()
    {
        // Load-bearing for throughput: an HTTP/2 host would otherwise multiplex every
        // range request onto a single TCP connection.
        EnableMultipleHttp2Connections = true,
        MaxConnectionsPerServer = maxConnectionsPerServer,
        // Archives are already compressed, and transparent decompression would break
        // byte-range accounting.
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(30),
        // Bounds draining an abandoned response body so its connection can be pooled. Reads
        // are bounded separately, per read, by RetryOptions.ReadTimeout.
        ResponseDrainTimeout = TimeSpan.FromSeconds(10),
    };

    public static HttpClient Create(int maxConnectionsPerServer) =>
        new(CreateHandler(maxConnectionsPerServer), disposeHandler: true)
        {
            // Whole-operation timeouts do not suit multi-gigabyte transfers.
            Timeout = Timeout.InfiniteTimeSpan,
        };

    /// <summary>
    /// One client for the whole application. MaxConnectionsPerServer must be at least
    /// DownloadOptions.Connections, or workers block in the connection pool and the download
    /// runs at a fraction of its configured parallelism. Connections is a live setting, so the
    /// pool is sized for the largest value it can ever hold rather than for whichever one was
    /// configured at startup. The engine still opens one connection per worker and spawns only
    /// Connections workers.
    /// </summary>
    public static HttpClient Create() => Create(DownloadOptions.MaxConnections);
}
