using System;
using System.Collections.Generic;
using System.IO;

namespace LamSims.Core.Downloading;

/// <summary>
/// Startup cleanup of partial downloads for packs no longer in the catalog, and of digest
/// records that describe an archive which is no longer there.
/// Quarantined archives (.zip.bad) are never deleted automatically. They are the evidence for
/// a checksum failure and are removed only on explicit user action.
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

            // Longest suffix first: '.part' would otherwise swallow '.part.json'. A code cannot
            // contain a path separator and no suffix is a prefix of another, so the match is
            // unambiguous.
            string code;
            var widowed = false;

            if (name.EndsWith(".part.json", StringComparison.Ordinal))
            {
                code = name[..^".part.json".Length];
            }
            else if (name.EndsWith(".zip.json", StringComparison.Ordinal))
            {
                code = name[..^".zip.json".Length];
            }
            else if (name.EndsWith(".part", StringComparison.Ordinal))
            {
                code = name[..^".part".Length];
            }
            else
            {
                continue;
            }

            // A file that is nothing but a suffix names no pack, and DownloadPaths refuses a
            // blank code rather than composing a path out of one.
            if (code.Length == 0)
                continue;

            if (name.EndsWith(".zip.json", StringComparison.Ordinal))
            {
                // A record describing an archive that is no longer there vouches for nothing.
                // Unlike a partial download, it is worth sweeping even for a known code.
                widowed = !File.Exists(_paths.ArchiveFile(code));
            }

            if (knownCodes.Contains(code) && !widowed)
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
