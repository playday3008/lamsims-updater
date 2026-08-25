using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LamSims.Core;

/// <summary>
/// The two rules every comparison between a catalog string and a filesystem string follows.
/// They govern NAMES — a pack directory, a file — and not the identity of a directory itself:
/// see <see cref="DirectoryKey"/>, which deliberately keeps the second rule out.
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
    /// A short, filesystem-safe name for a directory, used to name the file or folder holding
    /// that directory's records. A path cannot be a directory name, so it is hashed; the record
    /// still holds the path in full and every reader re-checks it, so a truncation collision
    /// cannot make a record vouch for the wrong directory.
    ///
    /// Case is significant, unlike the OrdinalIgnoreCase rule this class applies to names WITHIN
    /// a directory. Those are one filesystem entry read two ways; this is the identity of the
    /// directory itself, and on Linux "/games/Prefix" and "/games/prefix" are two directories
    /// that must not share a key. Folding them cost the first one its record outright: the second
    /// install overwrote it, and the first's removal then found nothing to undo and left a
    /// registry override in place for ever.
    ///
    /// What it costs on a case-insensitive filesystem is the reverse and much milder: one
    /// directory named two ways keys twice, so records written under one spelling are not found
    /// under the other. That is a miss, never a merge, and it recovers by rescanning.
    /// </summary>
    public static string DirectoryKey(string gameDirectory)
    {
        var canonical = Canonical(gameDirectory) ?? gameDirectory;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        return Convert.ToHexString(digest, 0, 8).ToLowerInvariant();
    }
}
