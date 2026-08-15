using System.Security.Cryptography;
using System.Text;

namespace LamSims.Core;

/// <summary>
/// The two rules every comparison between a catalog string and a filesystem string follows.
///
/// NFC first: an HFS+/APFS volume hands back the decomposed (NFD) form of a name the catalog
/// carries composed, and those two strings are not ordinally equal, so a successfully
/// installed "Sims 4 Café" would otherwise read as absent forever on macOS.
///
/// Then OrdinalIgnoreCase: pack directories written by a Windows install are routinely read
/// from a case-sensitive filesystem through Wine or a shared mount. Full canonical
/// equivalence beyond NFC/NFD is out of scope.
/// </summary>
internal static class PathIdentity
{
    /// <summary>
    /// NFC where the runtime can produce it, the original string where it cannot. A name on a
    /// Linux filesystem is an arbitrary byte sequence, so it can surface as invalid Unicode (a
    /// lone surrogate), and <see cref="string.Normalize(NormalizationForm)"/> throws on those.
    /// A damaged or hostile name must degrade to an ordinal comparison, never take a scan down
    /// with it.
    /// </summary>
    public static string Normalize(string value)
    {
        try
        {
            return value.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return value;
        }
    }

    /// <summary>
    /// The absolute, separator-trimmed, NFC form of a directory path, or null when the value
    /// cannot be resolved: blank, or rejected by the path APIs. Null propagates as "does not
    /// match anything" rather than as an exception, because one side of every comparison is a
    /// string read off disk.
    /// </summary>
    public static string? Canonical(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            return Normalize(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException
                                       or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// A short, filesystem-safe name for a game directory, used to group that directory's
    /// install markers. A path cannot be a directory name, so it is hashed; the marker still
    /// records the path in full and the scanner re-checks it, so a collision cannot make a
    /// marker vouch for the wrong directory.
    ///
    /// Lower-cased before hashing so the grouping agrees with the OrdinalIgnoreCase comparison
    /// that decides applicability. A character where the two disagree costs a group lookup that
    /// finds nothing, leaving the pack unverified rather than misattributed.
    /// </summary>
    public static string DirectoryKey(string gameDirectory)
    {
        var canonical = Canonical(gameDirectory) ?? gameDirectory;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToLowerInvariant()));

        return Convert.ToHexString(digest, 0, 8).ToLowerInvariant();
    }
}
