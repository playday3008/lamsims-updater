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
    /// <summary>A genuine remote fetch is bounded by this many bytes; see <see cref="ReadBoundedAsync"/>.</summary>
    private const long MaxRemoteBytes = 1024 * 1024;

    private readonly HttpClient _client;
    private readonly AppPaths _paths;
    private readonly string _executableDirectory;

    public CatalogLoader(HttpClient client, AppPaths paths, string? executableDirectory = null)
    {
        _client = client;
        _paths = paths;
        _executableDirectory = executableDirectory ?? AppContext.BaseDirectory;
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
                    CatalogStatus.Loaded, await LoadAsync(candidate, ct), candidate, null, cachedCopy);
            }
            catch (Exception e) when (
                (e is CatalogFormatException or IOException or HttpRequestException
                    or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                // HttpClient's own Timeout surfaces as a TaskCanceledException that does not
                // wrap into HttpRequestException. It is a source failure, not a cancellation,
                // unless the caller's own token is the one that fired — that must still
                // propagate rather than being reported as this source's problem.
                || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                // Only the first two sources are the user's explicit choice. A failure there is
                // reported, and the cached copy is offered as a choice rather than substituted.
                // The last two are fallbacks nobody asked for, so a broken one is passed over.
                if (candidate.Kind is CatalogSourceKind.CommandLine or CatalogSourceKind.Settings)
                {
                    return new CatalogResolution(
                        CatalogStatus.Failed, null, candidate,
                        $"The catalog at '{candidate.Location}' could not be loaded: {e.Message}",
                        cachedCopy);
                }
            }
        }

        return new CatalogResolution(CatalogStatus.Empty, null, null, null, cachedCopy);
    }

    public async Task<CatalogLoadResult> LoadAsync(CatalogSource source, CancellationToken ct)
    {
        if (!source.IsRemote)
            return CatalogParser.Parse(await File.ReadAllTextAsync(source.Location, ct));

        using var response = await _client.GetAsync(source.Location, ct);
        response.EnsureSuccessStatusCode();

        var json = await ReadBoundedAsync(response, source, ct);

        // Parsed before it is cached, so a mirror serving an error page never becomes the
        // copy the application falls back to when it is offline.
        var result = CatalogParser.Parse(json);
        await CacheAsync(json, ct);
        return result;
    }

    /// <summary>
    /// Reads the response body up to <see cref="MaxRemoteBytes"/>, past which a hostile or
    /// misconfigured URL could otherwise buffer without limit. A catalog is at most a few
    /// hundred KB, so the cap costs nothing a real catalog would ever hit.
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
            if (buffered.Length + read > MaxRemoteBytes)
            {
                throw new CatalogTooLargeException(
                    $"The catalog at '{source.Location}' exceeds the {MaxRemoteBytes}-byte limit.");
            }

            await buffered.WriteAsync(chunk.AsMemory(0, read), ct);
        }

        return Encoding.UTF8.GetString(buffered.ToArray());
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
