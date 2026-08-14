namespace LamSims.Core.Downloading;

/// <summary>
/// Startup cleanup of partial downloads for packs no longer in the catalog.
/// Quarantined archives (.zip.bad) are never deleted automatically — they are the
/// evidence for a checksum failure and are removed only on explicit user action.
/// </summary>
public sealed class OrphanCleaner
{
    private readonly DownloadPaths _paths;

    public OrphanCleaner(DownloadPaths paths) => _paths = paths;

    public IReadOnlyList<string> CleanOrphans(IReadOnlySet<string> knownCodes)
    {
        if (!Directory.Exists(_paths.Root))
            return Array.Empty<string>();

        var deleted = new List<string>();

        foreach (var file in Directory.EnumerateFiles(_paths.Root))
        {
            var name = Path.GetFileName(file);

            string code;
            if (name.EndsWith(".part.json", StringComparison.Ordinal))
                code = name[..^".part.json".Length];
            else if (name.EndsWith(".part", StringComparison.Ordinal))
                code = name[..^".part".Length];
            else
                continue;

            if (knownCodes.Contains(code))
                continue;

            try
            {
                File.Delete(file);
                deleted.Add(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A file locked by another instance, or one we lack permission to remove,
                // is left alone; the next run retries it. UnauthorizedAccessException does
                // not derive from IOException, so it needs naming explicitly.
            }
        }

        return deleted;
    }
}
