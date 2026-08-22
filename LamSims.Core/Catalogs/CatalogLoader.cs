using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using LamSims.Core.Settings;

namespace LamSims.Core.Catalogs;

/// <summary>The four sources, in the order they are tried.</summary>
public enum CatalogSourceKind { CommandLine, Settings, BesideExecutable, Cache }

public sealed record CatalogSource(CatalogSourceKind Kind, string Location)
{
    public bool IsRemote =>
        Uri.TryCreate(Location, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

public enum CatalogStatus { Loaded, Empty, Failed }

/// <summary>The catalog is too large to be a genuine catalog and was refused unparsed.</summary>
public sealed class CatalogTooLargeException : IOException
{
    public CatalogTooLargeException(string message) : base(message) { }
}

/// <summary>
/// The outcome of resolution. <see cref="CatalogStatus.Empty"/> is not an error: it means
/// the user has not chosen a catalog yet, and the pack list shows an empty state.
/// <see cref="CachedCopy"/> is the cached catalog offered as an explicit choice, never
/// substituted automatically. It is the source rather than a flag because the caller needs
/// it to load the copy.
/// </summary>
public sealed record CatalogResolution(
    CatalogStatus Status,
    CatalogLoadResult? Load,
    CatalogSource? Source,
    string? Error,
    CatalogSource? CachedCopy);

public sealed class CatalogLoader
{
    /// <summary>
    /// A genuine catalog is bounded by this many bytes, wherever it comes from. A local path is
    /// no more trustworthy than a URL, since both are values the user typed, so the same cap
    /// applies to each; see <see cref="ReadBoundedAsync"/> and <see cref="ReadLocalBoundedAsync"/>.
    /// </summary>
    private const long MaxCatalogBytes = 1024 * 1024;

    private static readonly TimeSpan DefaultRemoteTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _client;
    private readonly AppPaths _paths;
    private readonly string _executableDirectory;
    private readonly TimeSpan _remoteTimeout;

    /// <param name="remoteTimeout">
    /// Bounds a remote fetch end to end (headers and body). <see cref="HttpFactory"/> hands out
    /// an <see cref="HttpClient"/> with an infinite <see cref="HttpClient.Timeout"/> by design,
    /// because the download engine carries its own per-read deadlines. A client built for
    /// downloads and reused here for the catalog would otherwise hang forever against a mirror
    /// that accepts the connection and says nothing. A catalog is capped at 1 MB, so the
    /// 30-second default is generous.
    /// </param>
    public CatalogLoader(HttpClient client, AppPaths paths, string? executableDirectory = null, TimeSpan? remoteTimeout = null)
    {
        _client = client;
        _paths = paths;
        _executableDirectory = executableDirectory ?? AppContext.BaseDirectory;
        _remoteTimeout = remoteTimeout ?? DefaultRemoteTimeout;
    }

    /// <summary>
    /// The sources to try, in order. A command-line or settings value counts as present
    /// whenever it is non-blank, even when nothing is there: the user named it, and a
    /// silent fall-through would hide a typo.
    /// </summary>
    public IReadOnlyList<CatalogSource> Candidates(string? commandLine, string? fromSettings)
    {
        var candidates = new List<CatalogSource>();

        if (!string.IsNullOrWhiteSpace(commandLine))
            candidates.Add(new CatalogSource(CatalogSourceKind.CommandLine, commandLine));

        if (!string.IsNullOrWhiteSpace(fromSettings))
            candidates.Add(new CatalogSource(CatalogSourceKind.Settings, fromSettings));

        var beside = Path.Combine(_executableDirectory, "catalog.json");
        if (File.Exists(beside))
            candidates.Add(new CatalogSource(CatalogSourceKind.BesideExecutable, beside));

        if (File.Exists(_paths.CatalogCacheFile))
            candidates.Add(new CatalogSource(CatalogSourceKind.Cache, _paths.CatalogCacheFile));

        return candidates;
    }

    /// <summary>
    /// Propagates the caller's own cancellation as <see cref="OperationCanceledException"/>
    /// rather than folding it into a <see cref="CatalogStatus.Failed"/> result.
    /// </summary>
    public async Task<CatalogResolution> ResolveAsync(
        string? commandLine, string? fromSettings, CancellationToken ct)
    {
        var cachedCopy = File.Exists(_paths.CatalogCacheFile)
            ? new CatalogSource(CatalogSourceKind.Cache, _paths.CatalogCacheFile)
            : null;

        foreach (var candidate in Candidates(commandLine, fromSettings))
        {
            try
            {
                return new CatalogResolution(
                    CatalogStatus.Loaded, await LoadCoreAsync(candidate, ct), candidate, null, cachedCopy);
            }
            catch (Exception e) when (IsSourceFailure(e, ct))
            {
                // A cache that just failed to load during this same resolution is known broken,
                // so it must not be offered as the copy to fall back on. Nothing else failing
                // tells us anything about the cache, so cachedCopy otherwise stays as computed
                // above: a file that exists but was never tried.
                if (candidate.Kind == CatalogSourceKind.Cache)
                    cachedCopy = null;

                // Only the first two sources are the user's explicit choice. A failure there is
                // reported, and the cached copy is offered as a choice rather than substituted.
                // The last two are fallbacks nobody asked for, so a broken one is passed over.
                if (candidate.Kind is CatalogSourceKind.CommandLine or CatalogSourceKind.Settings)
                {
                    return new CatalogResolution(
                        CatalogStatus.Failed, null, candidate, FailureMessage(candidate, e), cachedCopy);
                }
            }
        }

        return new CatalogResolution(CatalogStatus.Empty, null, null, null, cachedCopy);
    }

    /// <summary>
    /// Loads exactly the source named by <paramref name="source"/>, whether that is the cached
    /// copy the user chose or a retry of a source that just failed. Never throws for a failure
    /// to load the source; it comes back as <see cref="CatalogStatus.Failed"/> naming
    /// <paramref name="source"/>, the same shape <see cref="ResolveAsync"/> returns. A
    /// caller-requested cancellation through <paramref name="ct"/> still propagates as
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<CatalogResolution> LoadAsync(CatalogSource source, CancellationToken ct)
    {
        try
        {
            return new CatalogResolution(
                CatalogStatus.Loaded, await LoadCoreAsync(source, ct), source, null, null);
        }
        catch (Exception e) when (IsSourceFailure(e, ct))
        {
            return new CatalogResolution(CatalogStatus.Failed, null, source, FailureMessage(source, e), null);
        }
    }

    /// <summary>
    /// True for a source failure: a format, I/O, network or path problem, or this loader's own
    /// deadline expiring. HttpClient's own Timeout, and the linked deadline in
    /// <see cref="LoadCoreAsync"/>, surface as <see cref="OperationCanceledException"/>, which
    /// does not wrap into <see cref="HttpRequestException"/>. False when the caller's own token
    /// is the one that fired, so that still propagates rather than being reported as this
    /// source's problem.
    /// </summary>
    private static bool IsSourceFailure(Exception e, CancellationToken ct) =>
        e is CatalogFormatException or IOException or HttpRequestException
            or UnauthorizedAccessException or ArgumentException or NotSupportedException
        || (e is OperationCanceledException && !ct.IsCancellationRequested);

    private static string FailureMessage(CatalogSource source, Exception e) =>
        $"The catalog at '{source.Location}' could not be loaded: {e.Message}";

    private async Task<CatalogLoadResult> LoadCoreAsync(CatalogSource source, CancellationToken ct)
    {
        if (!source.IsRemote)
            return CatalogParser.Parse(await ReadLocalBoundedAsync(source, ct));

        // Bounds the fetch end to end; see the constructor's remoteTimeout parameter. Firing
        // cancels the linked token, not the caller's ct, so IsSourceFailure's check of
        // ct.IsCancellationRequested still tells the two apart.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_remoteTimeout);

        // ResponseHeadersRead: without it, HttpClient buffers the entire body itself before
        // GetAsync even returns, and the size cap below would run only after a hostile or
        // misconfigured URL had already been read into memory in full.
        using var response = await _client.GetAsync(
            source.Location, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();

        var json = await ReadBoundedAsync(response, source, deadline.Token);

        // Parsed before it is cached, so a mirror serving an error page never becomes the
        // copy the application falls back to when it is offline.
        var result = CatalogParser.Parse(json);
        await CacheAsync(json, ct);
        return result;
    }

    /// <summary>
    /// The local counterpart of <see cref="ReadBoundedAsync"/>. Reading a path with
    /// File.ReadAllTextAsync would allocate whatever it was pointed at in full, and
    /// OutOfMemoryException is not something <see cref="IsSourceFailure"/> can report; the
    /// process dies. The length is known up front here, so the file is refused before any of it
    /// is read. detectEncodingFromByteOrderMarks matches the remote path so a BOM-prefixed
    /// catalog loads identically from disk and over HTTP.
    /// </summary>
    private static async Task<string> ReadLocalBoundedAsync(CatalogSource source, CancellationToken ct)
    {
        await using var stream = new FileStream(
            source.Location, FileMode.Open, FileAccess.Read, FileShare.Read);

        if (stream.Length > MaxCatalogBytes)
        {
            throw new CatalogTooLargeException(
                $"The catalog at '{source.Location}' exceeds the {MaxCatalogBytes}-byte limit.");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct);
    }

    /// <summary>
    /// Streams the response body into memory up to <see cref="MaxCatalogBytes"/>, past which a
    /// hostile or misconfigured URL is refused before more of it is read. This only bounds
    /// memory because <see cref="LoadCoreAsync"/> requests the response with
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/>; otherwise HttpClient would
    /// already hold the whole body before this method ever saw it.
    /// </summary>
    private static async Task<string> ReadBoundedAsync(
        HttpResponseMessage response, CatalogSource source, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffered = new MemoryStream();
        var chunk = new byte[8192];

        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffered.Length + read > MaxCatalogBytes)
            {
                throw new CatalogTooLargeException(
                    $"The catalog at '{source.Location}' exceeds the {MaxCatalogBytes}-byte limit.");
            }

            await buffered.WriteAsync(chunk.AsMemory(0, read), ct);
        }

        buffered.Position = 0;

        // detectEncodingFromByteOrderMarks mirrors File.ReadAllTextAsync's own handling of a
        // leading BOM, so a catalog authored with one (Notepad, PowerShell's Out-File default)
        // loads the same way over HTTP as it does from disk, instead of reaching
        // JsonDocument.Parse as a U+FEFF it rejects.
        using var reader = new StreamReader(buffered, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct);
    }

    private async Task CacheAsync(string json, CancellationToken ct)
    {
        try
        {
            _paths.EnsureCreated();
            await AtomicFile.WriteAllTextAsync(_paths.CatalogCacheFile, json, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A catalog that loaded is usable whether or not it can be cached; the only cost
            // of failing here is that the next offline launch has nothing to fall back to.
        }
    }
}
