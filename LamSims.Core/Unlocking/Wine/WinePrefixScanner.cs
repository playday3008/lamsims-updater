using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LamSims.Core.Unlocking.Wine;

/// <param name="Detail">
/// What identifies this candidate within its source, before dedup. Several candidates can name one
/// prefix, so this is a proposal rather than the final label.
/// </param>
public sealed record PrefixCandidate(string Path, EnvironmentSource Source, string Detail,
                                     bool Flatpak);

/// <summary>
/// Finds Wine prefixes by asking each launcher's own configuration, then validating what it finds.
/// Extract loosely, validate strictly: a wrong extraction is harmless because
/// <see cref="WinePrefix.TryOpen"/> rejects it with a reason, and that is what makes line-oriented
/// parsing safe enough to need no YAML or VDF dependency.
///
/// Every extraction source is best-effort. An unreadable or malformed config yields a diagnostic
/// and no candidates, never an exception: one broken game file must not stop the scan.
/// </summary>
public sealed class WinePrefixScanner(LauncherHomes homes, string userName,
                                      IProgress<string>? notes = null,
                                      string flatpakInfoFile = "/.flatpak-info")
{
    public IReadOnlyList<WinePrefix> Scan(string? configuredPrefix)
    {
        var candidates = new List<PrefixCandidate>();

        // The user's own setting first, so a prefix they named explicitly is never crowded out.
        if (!string.IsNullOrWhiteSpace(configuredPrefix))
            candidates.Add(new PrefixCandidate(configuredPrefix, EnvironmentSource.Wine,
                                               configuredPrefix, false));

        candidates.AddRange(FromWine());

        candidates.AddRange(FromSteam());

        var found = Describe(candidates);

        if (found.Count == 0 && File.Exists(flatpakInfoFile))
        {
            notes?.Report("This build runs in a Flatpak sandbox and cannot see host paths "
                          + "without --filesystem=home.");
        }

        return found;
    }

    /// <summary>
    /// Dedup, label and open. Internal rather than private so a test can drive the labelling rules
    /// directly: they are decided across candidates, and reaching them through six extraction
    /// sources would make a labelling failure read as a discovery failure.
    /// </summary>
    internal IReadOnlyList<WinePrefix> Describe(IReadOnlyList<PrefixCandidate> candidates)
    {
        var opened = new List<WinePrefix>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Grouped by RESOLVED path, not canonical: ~/.steam/steam is a symlink to
        // <data>/Steam on a real machine, and PathIdentity.Canonical is Path.GetFullPath, which
        // never resolves a link — so canonical-only dedup shows every Steam prefix twice.
        foreach (var group in candidates.GroupBy(c => Resolve(c.Path) ?? c.Path,
                                                 StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            var count = group.Count();

            // Several configs routinely name one prefix, and they disagree about which game it is,
            // so a name taken from one of them is not reproducible between runs. The launcher and
            // the count are.
            var detail = count > 1
                ? $"{count} games"
                : first.Detail is { Length: > 0 } named ? named : DirectoryName(group.Key);

            var prefix = WinePrefix.TryOpen(group.Key, new TargetEnvironment(
                first.Source, detail, first.Flatpak), userName, notes);
            if (prefix is null) continue;

            // The resolved path is the group key, but TryOpen may have descended into `pfx`, so the
            // final identity check uses what it actually opened.
            if (!seen.Add(PathIdentity.Canonical(prefix.Root) ?? prefix.Root)) continue;

            opened.Add(prefix);
        }

        return opened;
    }

    private static string DirectoryName(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));

        return name.Length > 0 ? name : path;
    }

    /// <summary>
    /// The physical path, with EVERY symlinked component resolved — not just the last one.
    ///
    /// <c>Directory.ResolveLinkTarget</c> follows a link only in the final component, and the link
    /// that matters here is routinely in the middle: <c>~/.steam/steam</c> is a symlink to
    /// <c>&lt;data&gt;/Steam</c> on a real machine, and Steam candidates are
    /// <c>&lt;root&gt;/steamapps/compatdata/*/pfx</c> underneath it. Calling ResolveLinkTarget on
    /// the candidate returns null there — <c>pfx</c> is not itself a link — so the two spellings of
    /// one prefix stay distinct and every Steam prefix appears twice, which is the exact defect
    /// deduplication exists to prevent.
    /// </summary>
    private static string? Resolve(string path)
    {
        var canonical = PathIdentity.Canonical(path);
        if (canonical is null) return null;

        try
        {
            // Walked from the path root so the drive or leading separator survives: on Windows the
            // root is "C:\\" and splitting it away would produce a path relative to the working
            // directory.
            var root = Path.GetPathRoot(canonical) ?? "";
            var current = canonical;
            var passes = 0;
            const int maxPasses = 40; // Linux caps ELOOP at roughly this; the cap is what makes a
                                       // self-referential link terminate instead of hanging.

            while (passes < maxPasses)
            {
                passes++;

                // Walk from root looking for the first link.
                var cursor = root;
                var parts = current[root.Length..]
                    .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

                var foundLink = false;
                for (var i = 0; i < parts.Length; i++)
                {
                    cursor = Path.Combine(cursor, parts[i]);

                    // DirectoryInfo and FileInfo both read the link data, so one of the two
                    // answers for either kind, and neither throws on a component that does not
                    // exist or whose target is missing.
                    var target = new DirectoryInfo(cursor).LinkTarget
                                 ?? new FileInfo(cursor).LinkTarget;
                    if (target is null) continue;

                    // Found a link. Resolve it and re-append the untraversed components.
                    var resolved = PathIdentity.Canonical(Path.IsPathRooted(target)
                                      ? target
                                      : Path.Combine(Path.GetDirectoryName(cursor) ?? root, target))
                                  ?? cursor;

                    // Re-append any untraversed components.
                    var remaining = string.Join(Path.DirectorySeparatorChar.ToString(),
                                               parts[(i + 1)..]);
                    current = remaining.Length > 0
                        ? Path.Combine(resolved, remaining)
                        : resolved;

                    foundLink = true;
                    break; // Restart the walk from the beginning with the new path.
                }

                if (!foundLink) break; // No link found in this pass; path is final.
            }

            return PathIdentity.Canonical(current);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or ArgumentException)
        {
            // An unreadable component leaves the path unresolved rather than dropping the
            // candidate: validation is what rejects a bad path, not this.
            return canonical;
        }
    }

    private IEnumerable<PrefixCandidate> FromWine()
    {
        if (homes.WinePrefixEnvironment is { } fromEnvironment)
            yield return new PrefixCandidate(fromEnvironment, EnvironmentSource.Wine,
                                             "$WINEPREFIX", false);

        foreach (var home in homes.For(EnvironmentSource.Wine))
        {
            // ~/.wine IS a prefix; <data>/wineprefixes and the Flatpak wine directory are
            // containers of them. Both shapes are offered and validation sorts them out.
            yield return new PrefixCandidate(home.Root, EnvironmentSource.Wine,
                                             DirectoryName(home.Root), home.Flatpak);

            foreach (var child in Children(home.Root))
                yield return new PrefixCandidate(child, EnvironmentSource.Wine,
                                                 DirectoryName(child), home.Flatpak);
        }
    }

    /// <summary>One level. Never a recursive walk: a prefix's own drive_c would swallow the scan.</summary>
    internal static IEnumerable<string> Children(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The first <c>key: value</c> in a line-oriented config, unquoted and with an inline comment
    /// stripped. Deliberately not a YAML parser: no dependency may be added, and a wrong extraction
    /// is rejected by validation with a reason.
    /// </summary>
    internal static string? ReadLineValue(string file, string key)
    {
        foreach (var value in ReadLineValues(file, key)) return value;

        return null;
    }

    internal static IEnumerable<string> ReadLineValues(string file, string key)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!line.StartsWith($"{key}:", StringComparison.OrdinalIgnoreCase)) continue;

            var value = Clean(line[(key.Length + 1)..]);
            if (value is not null) yield return value;
        }
    }

    private static string? Clean(string value)
    {
        value = value.Trim();

        if (value.StartsWith('"') || value.StartsWith('\''))
        {
            var quote = value[0];
            var end = value.IndexOf(quote, 1);
            return end > 1 ? value[1..end] : null;
        }

        var comment = value.IndexOf('#');
        if (comment >= 0) value = value[..comment].TrimEnd();

        return value.Length == 0 ? null : value;
    }

    internal void Note(string message) => notes?.Report(message);

    private IEnumerable<PrefixCandidate> FromSteam()
    {
        foreach (var home in homes.For(EnvironmentSource.Steam))
        {
            foreach (var library in Libraries(home.Root))
            {
                var compatdata = Path.Combine(library, "steamapps", "compatdata");

                foreach (var container in Children(compatdata))
                {
                    var appId = Path.GetFileName(container);

                    // Not an app. Without this every machine with Steam reports a phantom target or
                    // a rejection note about one.
                    if (appId == "0") continue;

                    yield return new PrefixCandidate(Path.Combine(container, "pfx"),
                                                     EnvironmentSource.Steam,
                                                     Label(container, appId), home.Flatpak);
                }
            }
        }
    }

    /// <summary>
    /// The root itself plus every path in its own library list. A Flatpak Steam's list is its own,
    /// which is why this is keyed off the home rather than read once.
    /// </summary>
    private IEnumerable<string> Libraries(string root)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = PathIdentity.Canonical(root) ?? root;
        if (seen.Add(resolved)) yield return root;

        var file = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(file)) yield break;

        foreach (var path in VdfValues(file, "path"))
        {
            var pathResolved = PathIdentity.Canonical(path) ?? path;
            if (seen.Add(pathResolved)) yield return path;
        }
    }

    /// <summary>
    /// Every <c>"key" "value"</c> pair with this key. VDF is quoted and tab-separated, and its
    /// values carry <c>\\</c> and <c>\"</c> escapes that a raw read would leave in a path.
    /// </summary>
    private IEnumerable<string> VdfValues(string file, string key)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Note($"Steam: '{file}' could not be read: {e.Message}.");
            return [];
        }

        return lines.Select(line => VdfPair(line, key)).Where(v => v is not null).Select(v => v!);
    }

    private static string? VdfPair(string line, string key)
    {
        var trimmed = line.Trim();
        var quoted = $"\"{key}\"";
        if (!trimmed.StartsWith(quoted, StringComparison.OrdinalIgnoreCase)) return null;

        var rest = trimmed[quoted.Length..];
        var open = rest.IndexOf('"');
        if (open < 0) return null;

        var builder = new System.Text.StringBuilder();
        for (var i = open + 1; i < rest.Length; i++)
        {
            if (rest[i] == '\\' && i + 1 < rest.Length) { builder.Append(rest[++i]); continue; }
            if (rest[i] == '"') return builder.Length == 0 ? null : builder.ToString();
            builder.Append(rest[i]);
        }

        return null;
    }

    /// <summary>
    /// The build is NOT config_info line 1 in general: GE-Proton writes its own name there, and
    /// Valve Proton writes a version number with the build recoverable only from the later path
    /// lines (<c>common/Proton - Experimental/files/...</c>). Taking line 1 unconditionally labels
    /// every Valve prefix with a version that names no build.
    /// </summary>
    private static string Label(string container, string appId)
    {
        var build = BuildFromConfigInfo(Path.Combine(container, "config_info"))
                    ?? FirstLine(Path.Combine(container, "config_info"))
                    ?? FirstLine(Path.Combine(container, "version"));

        return build is null ? $"app {appId}" : $"{build}, app {appId}";
    }

    private static string? BuildFromConfigInfo(string file)
    {
        const string marker = "steamapps/common/";

        foreach (var line in Lines(file))
        {
            var at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) continue;

            var rest = line[(at + marker.Length)..];
            var end = rest.IndexOf('/');
            if (end > 0) return rest[..end];
        }

        return null;
    }

    private static string? FirstLine(string file)
    {
        foreach (var line in Lines(file))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed;
        }

        return null;
    }

    private static IEnumerable<string> Lines(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllLines(file) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
