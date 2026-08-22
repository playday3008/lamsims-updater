using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LamSims.Core.Catalogs;
using LamSims.Core.Installing;

namespace LamSims.Core.Scanning;

/// <summary>
/// What is known about a pack on disk, ordered worst to best so a caller can sort by rank.
/// <see cref="InstalledUnverified"/> is every install this tool did not perform: one made by
/// another tool, one from before the journal existed, or one whose marker was lost with the
/// config directory. It belongs to the installed family and is never a warning.
/// </summary>
public enum PackInstallState { NotInstalled, Partial, InstalledUnverified, Installed }

/// <summary>
/// One pack's state. An empty <see cref="MissingDirs"/> together with
/// <see cref="PackInstallState.Partial"/> is the contract for "an install started and never
/// finished": every install directory is present and the journal says the extraction did not
/// complete. <see cref="InstalledSha256"/> and <see cref="InstalledUtc"/> are populated only
/// for <see cref="PackInstallState.Installed"/>; the recorded digest is what lets a caller
/// derive "update available" by comparing it against the catalog's, with no state of its own.
/// </summary>
public sealed record PackScanResult(
    string Code,
    PackInstallState State,
    IReadOnlyList<string> MissingDirs,
    string? InstalledSha256,
    DateTimeOffset? InstalledUtc);

/// <summary>
/// The outcome of a scan. <see cref="GameDirectoryReadable"/> is false only when the game
/// directory is absent or enumerating it failed, never merely because it turned out to be
/// empty. Every pack in <see cref="Packs"/> reads <see cref="PackInstallState.NotInstalled"/>
/// in both cases, so without this flag a caller cannot tell an unreadable directory (behind
/// a permission error) from one that is genuinely empty.
/// </summary>
public sealed record ScanResult(bool GameDirectoryReadable, IReadOnlyList<PackScanResult> Packs);

/// <summary>
/// Detects installed packs by comparing directory <em>names</em> under the game directory
/// against each pack's install directories, then consulting the install journal for the packs
/// whose directories are all present. Names rather than a substring of the full path, so a
/// game at <c>D:\SP20\Sims 4</c> does not read as having SP20 installed.
///
/// Directories are ground truth for absence and the journal is ground truth for completion.
/// Neither alone is enough: a cancelled install leaves the directory it had started writing,
/// and a marker outlives the install it describes.
/// </summary>
public static class InstallScanner
{
    /// <summary>
    /// Pure and total: never writes a marker, never throws on one's content, and never
    /// consults markers at all when the game directory could not be read, so state cannot
    /// claim a presence the scan could not confirm.
    /// </summary>
    /// <param name="markers">
    /// The markers recorded for <paramref name="gameDirectory"/>, from
    /// <see cref="InstallStateStore.LoadAll"/>. Its keys are pack codes compared
    /// case-insensitively; a dictionary built with an ordinal comparer would miss markers over
    /// nothing but case.
    /// </param>
    public static ScanResult Scan(
        string gameDirectory,
        IEnumerable<PackEntry> packs,
        IReadOnlyDictionary<string, InstallMarker> markers)
    {
        // Case-insensitive throughout: pack directories written by a Windows install are
        // routinely read from a case-sensitive filesystem through Wine or a shared mount.
        // NFC on top of that, because a volume can return a name in a different normal form
        // than the catalog carries it in; see PathIdentity.
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var readable = false;

        if (Directory.Exists(gameDirectory))
        {
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(gameDirectory))
                    present.Add(PathIdentity.Normalize(Path.GetFileName(directory)!));

                readable = true;
            }
            catch (IOException)
            {
                // Directory exists but is unreadable (permission denied) or was deleted between
                // the existence check and enumeration. Treat it as an absent directory: every pack
                // is NotInstalled. This keeps Scan total and handles the Wine scenario where the
                // game directory lives on a shared mount with intermittent access.
                //
                // EnumerateDirectories is lazy: a failure partway through still leaves the names
                // already yielded in the set. Clearing it is what makes every pack NotInstalled
                // here, matching ScanResult's contract instead of reporting some packs Installed
                // off a partial listing.
                present.Clear();
            }
            catch (UnauthorizedAccessException)
            {
                // Same as IOException: the directory exists but cannot be read.
                present.Clear();
            }
        }

        var results = new List<PackScanResult>();

        // Canonicalised once rather than inside Applicable: every pack in the catalog would
        // otherwise re-run Path.GetFullPath and an NFC pass over the same scanned directory.
        var canonicalGameDirectory = PathIdentity.Canonical(gameDirectory);

        foreach (var pack in packs)
        {
            var missing = pack.InstallDirs
                .Where(d => !present.Contains(PathIdentity.Normalize(d)))
                .ToArray();

            // Checked before the "nothing missing" case so a pack with no install directories
            // reads NotInstalled rather than installed.
            if (missing.Length == pack.InstallDirs.Count)
            {
                results.Add(new PackScanResult(pack.Code, PackInstallState.NotInstalled, missing, null, null));
                continue;
            }

            if (missing.Length > 0)
            {
                results.Add(new PackScanResult(pack.Code, PackInstallState.Partial, missing, null, null));
                continue;
            }

            var marker = Applicable(markers, canonicalGameDirectory, pack.Code);

            if (marker is null)
            {
                results.Add(new PackScanResult(pack.Code, PackInstallState.InstalledUnverified, missing, null, null));
            }
            else if (marker.Status == InstallMarkerStatus.Installed)
            {
                results.Add(new PackScanResult(
                    pack.Code, PackInstallState.Installed, missing, marker.ArchiveSha256, marker.UpdatedUtc));
            }
            else
            {
                // Every directory present and the journal still open: the extraction was
                // interrupted. Empty MissingDirs is how a caller tells this apart from the
                // ordinary subset case.
                results.Add(new PackScanResult(pack.Code, PackInstallState.Partial, missing, null, null));
            }
        }

        return new ScanResult(readable, results);
    }

    /// <summary>
    /// The marker for <paramref name="code"/>, but only if it vouches for this game directory.
    /// The store already groups markers by directory; re-checking the recorded path is what
    /// makes that grouping unable to misattribute state, so a hand-copied marker, or one whose
    /// directory has since been renamed, stops applying.
    /// </summary>
    /// <param name="canonicalGameDirectory">
    /// The scanned directory, already run through <see cref="PathIdentity.Canonical"/> by the
    /// caller. Null means it could not be resolved, which must still mean "does not apply"
    /// rather than throwing or matching a marker whose own directory is also unresolvable.
    /// </param>
    private static InstallMarker? Applicable(
        IReadOnlyDictionary<string, InstallMarker> markers, string? canonicalGameDirectory, string code)
    {
        if (!markers.TryGetValue(code, out var marker)) return null;
        if (canonicalGameDirectory is null) return null;

        var markerDirectory = PathIdentity.Canonical(marker.GameDirectory);

        return string.Equals(marker.Code, code, StringComparison.OrdinalIgnoreCase)
               && markerDirectory is not null
               && string.Equals(canonicalGameDirectory, markerDirectory, StringComparison.OrdinalIgnoreCase)
            ? marker
            : null;
    }
}
