namespace LamSims.Core.Downloading;

/// <summary>
/// What the engine needs to fetch one archive. Catalog entries are mapped onto this by their
/// caller; the engine itself knows nothing about catalogs.
/// </summary>
public sealed record DownloadRequest(string Code, IReadOnlyList<Uri> Urls, long Size, string Sha256);

public sealed record DownloadProgress(
    string Code,
    long BytesCompleted,
    long TotalBytes,
    double BytesPerSecond,
    TimeSpan? Eta);

public enum DownloadOutcome { Completed, ChecksumMismatch, Cancelled, Failed }

public sealed record DownloadResult(
    DownloadOutcome Outcome,
    string? FilePath,
    string? ExpectedSha256,
    string? ActualSha256,
    bool UsedSingleStream,
    string? Error)
{
    public static DownloadResult Completed(string filePath, string sha256, bool usedSingleStream) =>
        new(DownloadOutcome.Completed, filePath, sha256, sha256, usedSingleStream, null);

    public static DownloadResult ChecksumMismatch(string quarantinePath, string expected, string actual, bool usedSingleStream) =>
        new(DownloadOutcome.ChecksumMismatch, quarantinePath, expected, actual, usedSingleStream,
            $"Expected SHA-256 {expected} but the downloaded file hashes to {actual}.");

    public static DownloadResult Cancelled(bool usedSingleStream = false) =>
        new(DownloadOutcome.Cancelled, null, null, null, usedSingleStream, null);

    public static DownloadResult Failed(string error, bool usedSingleStream = false) =>
        new(DownloadOutcome.Failed, null, null, null, usedSingleStream, error);
}

public sealed class DownloadOptions
{
    private int _connections = 8;

    /// <summary>Connections used for one archive. One archive downloads at a time.</summary>
    public int Connections
    {
        get => _connections;
        set => _connections = value is >= 1 and <= 16
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Connections must be between 1 and 16.");
    }

    public long ChunkSize { get; set; } = ChunkPlan.DefaultChunkSize;
}
