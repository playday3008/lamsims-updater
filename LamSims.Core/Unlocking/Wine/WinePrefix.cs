using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LamSims.Core.Unlocking.Wine;

public enum WineArch { Win64, Win32 }

/// <summary>
/// One Wine prefix, opened and validated. Owns the Windows-to-Linux path translation, which is
/// the piece the rest of the Wine support cannot work without: on Linux
/// <c>Path.GetDirectoryName(@"C:\Program Files\EA\EADesktop.exe")</c> returns an EMPTY string,
/// because <c>\</c> is not a separator, so a caller handing a raw registry value to the engine
/// finds nothing on every prefix with no error at all.
/// </summary>
public sealed class WinePrefix
{
    private WinePrefix(string root, string driveC, WineArch arch, bool protonManaged,
                       TargetEnvironment environment, IReadOnlyList<string> userDirectories)
    {
        Root = root;
        DriveC = driveC;
        Arch = arch;
        ProtonManaged = protonManaged;
        Environment = environment;
        WindowsUserDirectories = userDirectories;
    }

    public string Root { get; }
    public string DriveC { get; }
    public WineArch Arch { get; }
    public bool ProtonManaged { get; }
    public TargetEnvironment Environment { get; }

    /// <summary>
    /// Every non-<c>Public</c> Windows user directory the unlocker configuration belongs under.
    /// Usually one; more when the prefix kind cannot say which, in which case writing
    /// each is idempotent and removes the guess.
    /// </summary>
    public IReadOnlyList<string> WindowsUserDirectories { get; }

    public string PrimaryUserDirectory => WindowsUserDirectories[0];

    public string UserRegFile => Path.Combine(Root, "user.reg");

    /// <summary>
    /// The three files that are evidence of a Proton or umu prefix, which uses
    /// <c>steamuser</c> rather than the invoking user's name.
    /// </summary>
    private static readonly string[] ProtonMarkers = ["version", "config_info", "tracked_files"];

    /// <param name="userName">
    /// Injected, never read from <see cref="System.Environment"/>, so discovery is testable —
    /// the same rule <see cref="UnlockerPaths"/> and <c>AppPaths</c> follow.
    /// </param>
    public static WinePrefix? TryOpen(string candidate, TargetEnvironment environment,
                                      string userName, IProgress<string>? notes = null)
    {
        var root = PathIdentity.Canonical(candidate);
        if (root is null) return null;

        var missing = MissingPart(root);
        if (missing is not null)
        {
            // One level, never recursive. GE-Proton and umu create `pfx` as a self-symlink so the
            // recorded path IS the prefix and this branch is not reached, while Valve Proton uses a
            // real `pfx/` subdirectory under the path the launcher recorded. Without the retry every
            // Valve-Proton-shaped Lutris, Heroic or Bottles prefix is rejected. Recursing instead
            // would make the scanner's deliberate one-level enumeration of Heroic's default
            // container meaningless and surface prefixes nobody configured.
            var nested = PathIdentity.Canonical(Path.Combine(root, "pfx"));
            if (nested is null || MissingPart(nested) is not null)
            {
                notes?.Report($"'{candidate}' is not a Wine prefix: no {missing}.");
                return null;
            }

            root = nested;
        }

        var driveC = Path.Combine(root, "drive_c");
        var proton = IsProtonManaged(root);

        return new WinePrefix(root, driveC, ReadArch(root, notes), proton, environment,
                              UserDirectories(root, driveC, proton, userName, notes));
    }

    private static string? MissingPart(string root) =>
        !File.Exists(Path.Combine(root, "system.reg")) ? "system.reg"
        : !Directory.Exists(Path.Combine(root, "drive_c")) ? "drive_c"
        : null;

    /// <summary>
    /// The markers sit at the prefix root for GE-Proton and umu, and one level UP for Valve
    /// Proton, whose prefix is <c>compatdata/&lt;id&gt;/pfx</c> while <c>version</c>,
    /// <c>config_info</c> and <c>tracked_files</c> live in <c>compatdata/&lt;id&gt;</c>. Checking
    /// only the root misses every Valve Proton prefix and then picks the wrong Windows user for
    /// the one population that always uses <c>steamuser</c>.
    /// </summary>
    private static bool IsProtonManaged(string root)
    {
        var parent = Path.GetDirectoryName(root);

        foreach (var marker in ProtonMarkers)
        {
            if (File.Exists(Path.Combine(root, marker))) return true;
            if (parent is not null && File.Exists(Path.Combine(parent, marker))) return true;
        }

        return false;
    }

    /// <summary>
    /// Capped at the header. <c>#arch=</c> is on the fourth line of a real prefix and
    /// <c>system.reg</c> runs to 38 000 lines, so an uncapped scan of a header-less file would read
    /// the whole registry to learn nothing.
    /// </summary>
    private const int HeaderLines = 10;

    private static WineArch ReadArch(string root, IProgress<string>? notes)
    {
        foreach (var file in new[] { "system.reg", "user.reg" })
        {
            try
            {
                var read = 0;
                foreach (var line in File.ReadLines(Path.Combine(root, file)))
                {
                    if (++read > HeaderLines) break;
                    if (!line.StartsWith("#arch=", StringComparison.Ordinal)) continue;

                    var value = line["#arch=".Length..].Trim();
                    if (string.Equals(value, "win32", StringComparison.OrdinalIgnoreCase))
                        return WineArch.Win32;
                    if (string.Equals(value, "win64", StringComparison.OrdinalIgnoreCase))
                        return WineArch.Win64;
                    break;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Fall through to the other file, then to the default. A prefix whose header
                // cannot be read still holds a client that can be unlocked.
            }
        }

        notes?.Report($"'{root}' does not declare a Wine architecture; it is treated as 64-bit.");
        return WineArch.Win64;
    }

    private static IReadOnlyList<string> UserDirectories(string root, string driveC, bool proton,
                                                        string userName, IProgress<string>? notes)
    {
        var users = Path.Combine(driveC, "users");
        var expected = proton ? "steamuser" : userName;

        string[] candidates;
        try
        {
            candidates = Directory.Exists(users)
                ? Directory.GetDirectories(users)
                    .Where(d => !string.Equals(Path.GetFileName(d), "Public",
                                               StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.Ordinal)
                    .ToArray()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            candidates = [];
        }

        // Nothing there yet. The join is returned so the install can create it, which is the same
        // rule ResolveWindowsPath applies to a missing final segment.
        if (candidates.Length == 0) return [Path.Combine(users, expected)];

        var match = candidates.FirstOrDefault(
            d => string.Equals(Path.GetFileName(d), expected, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return [match];

        if (candidates.Length == 1) return candidates;

        // Genuinely ambiguous: the kind-derived name is not among the users that exist, and more
        // than one does. Writing each is idempotent and removes the guess; guessing wrong puts the
        // configuration where the DLL will not look, which is a silent no-op behind a green UI.
        notes?.Report($"'{root}' has more than one Windows user; the configuration will be written under each.");
        return candidates;
    }
}
