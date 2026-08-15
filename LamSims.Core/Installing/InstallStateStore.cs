using System.Text.Json;
using System.Text.Json.Serialization;

namespace LamSims.Core.Installing;

public enum InstallMarkerStatus { Installing, Installed }

/// <summary>
/// One pack's install journal entry. <see cref="InstallMarkerStatus.Installing"/> is written
/// durably before the first game-directory mutation and rewritten only after the last entry
/// lands, so a marker still reading <c>Installing</c> is positive evidence that an extraction
/// was interrupted rather than an inference from what is missing.
/// </summary>
public sealed record InstallMarker(
    int SchemaVersion,
    string Code,
    string GameDirectory,
    string ArchiveSha256,
    InstallMarkerStatus Status,
    DateTimeOffset UpdatedUtc)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Persists install markers, one JSON file per pack code, grouped by game directory. Writes
/// are atomic and durable. Reads are total: an absent, torn, unparseable, wrong-code or
/// future-versioned marker is null rather than an exception, so a corrupt marker degrades a
/// pack to "installed, unverified" and can never accuse a healthy install of being
/// interrupted.
///
/// Markers are grouped by game directory rather than by code alone so that pointing the
/// GameDirectory setting at a second drive does not demote the first drive's library. Nothing
/// reaps a group. The only rules available, "not the directory in settings" and "the directory
/// is not on disk", would delete the state of a second drive or an unmounted share, and a
/// group costs a few hundred bytes per pack.
/// </summary>
public sealed class InstallStateStore
{
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Every field nullable, so an absent one is distinguishable from a present one holding
    /// the default. Deserializing straight into <see cref="InstallMarker"/> would read a
    /// missing <c>status</c> as <see cref="InstallMarkerStatus.Installing"/>, and a truncated
    /// write would then report a complete install as interrupted.
    /// </summary>
    private sealed record MarkerDocument(
        int? SchemaVersion,
        string? Code,
        string? GameDirectory,
        string? ArchiveSha256,
        InstallMarkerStatus? Status,
        DateTimeOffset? UpdatedUtc);

    public string Root { get; }

    public InstallStateStore(string directory) => Root = directory;

    public string MarkerFile(string gameDirectory, string code) =>
        Path.Combine(GroupDirectory(gameDirectory), ValidateCode(code).ToLowerInvariant() + ".json");

    public InstallMarker? TryLoad(string gameDirectory, string code)
    {
        try
        {
            return Read(MarkerFile(gameDirectory, code));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Every marker recorded for one game directory, keyed by the marker's own
    /// <see cref="InstallMarker.Code"/> and compared <see cref="StringComparer.OrdinalIgnoreCase"/>,
    /// the same way <see cref="Catalogs.CatalogParser"/> de-duplicates codes. Duplicate codes
    /// resolve to the greater <see cref="InstallMarker.UpdatedUtc"/> so the winner does not
    /// depend on directory listing order.
    /// </summary>
    public IReadOnlyDictionary<string, InstallMarker> LoadAll(string gameDirectory)
    {
        var markers = new Dictionary<string, InstallMarker>(StringComparer.OrdinalIgnoreCase);

        string group;
        try
        {
            group = GroupDirectory(gameDirectory);
        }
        catch (ArgumentException)
        {
            return markers;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(group))
            {
                // Filtered by suffix rather than by a search pattern: AtomicFile leaves
                // '<name>.json.<random>.tmp' beside a marker when a write is killed, and
                // parsing that would count one pack twice.
                if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

                var marker = Read(file);
                if (marker is null) continue;

                if (!markers.TryGetValue(marker.Code, out var existing) || marker.UpdatedUtc > existing.UpdatedUtc)
                    markers[marker.Code] = marker;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A missing group directory is the ordinary case for a game directory nothing has
            // been installed into. A fault partway through enumeration keeps whatever markers
            // were already read, which can only leave a pack unverified rather than accuse a
            // healthy one of being interrupted.
        }

        return markers;
    }

    /// <summary>
    /// Writes the marker for <see cref="InstallMarker.GameDirectory"/> and
    /// <see cref="InstallMarker.Code"/>. Throws on an I/O failure, because the two callers want
    /// opposite things: a failed intent write must fail the install, a failed completion write
    /// must not.
    /// </summary>
    public async Task SaveAsync(InstallMarker marker, CancellationToken ct)
    {
        var path = MarkerFile(marker.GameDirectory, marker.Code);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await AtomicFile.WriteAllTextAsync(path, JsonSerializer.Serialize(marker, Format), ct);
    }

    private string GroupDirectory(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
            throw new ArgumentException("Game directory must not be blank.", nameof(gameDirectory));

        return Path.Combine(Root, PathIdentity.DirectoryKey(gameDirectory));
    }

    private static InstallMarker? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var document = JsonSerializer.Deserialize<MarkerDocument>(File.ReadAllText(path), Format);

            if (document is null
                || document.SchemaVersion != InstallMarker.CurrentSchemaVersion
                || string.IsNullOrWhiteSpace(document.Code)
                || string.IsNullOrWhiteSpace(document.GameDirectory)
                || !Hex.IsSha256(document.ArchiveSha256)
                || document.Status is null
                || document.UpdatedUtc is null)
            {
                return null;
            }

            return new InstallMarker(
                document.SchemaVersion.Value, document.Code, document.GameDirectory,
                document.ArchiveSha256!, document.Status.Value, document.UpdatedUtc.Value);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException
                                       or NotSupportedException)
        {
            // JsonException also covers an unrecognised 'status' string: the converter rejects
            // it rather than picking a case, which is what keeps an unknown value from
            // becoming a claim about the install.
            return null;
        }
    }

    private static string ValidateCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Pack code must not be blank.", nameof(code));
        if (code.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"Pack code '{code}' contains path characters.", nameof(code));

        return code;
    }
}
