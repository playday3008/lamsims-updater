using LamSims.Core.Catalogs;

namespace LamSims.Core.Scanning;

public enum PackInstallState { NotInstalled, Partial, Installed }

public sealed record PackScanResult(string Code, PackInstallState State, IReadOnlyList<string> MissingDirs);

/// <summary>
/// Detects installed packs by comparing directory <em>names</em> under the game directory
/// against each pack's install directories. Upstream compared each pack code against the
/// full path as a substring, which reports SP20 as installed for a game at
/// <c>D:\SP20\Sims 4</c>.
/// </summary>
public static class InstallScanner
{
    public static IReadOnlyList<PackScanResult> Scan(string gameDirectory, IEnumerable<PackEntry> packs)
    {
        // Case-insensitive throughout: pack directories written by a Windows install are
        // routinely read from a case-sensitive filesystem through Wine or a shared mount.
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(gameDirectory))
        {
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(gameDirectory))
                    present.Add(Path.GetFileName(directory)!);
            }
            catch (IOException)
            {
                // Directory exists but is unreadable (permission denied) or was deleted between
                // the existence check and enumeration. Treat it as an absent directory: every pack
                // is NotInstalled. This keeps Scan total and handles the Wine scenario where the
                // game directory lives on a shared mount with intermittent access.
            }
            catch (UnauthorizedAccessException)
            {
                // Same as IOException: the directory exists but cannot be read.
            }
        }

        var results = new List<PackScanResult>();

        foreach (var pack in packs)
        {
            var missing = pack.InstallDirs.Where(d => !present.Contains(d)).ToArray();

            var state = missing.Length == 0
                ? PackInstallState.Installed
                : missing.Length == pack.InstallDirs.Count
                    ? PackInstallState.NotInstalled
                    : PackInstallState.Partial;

            results.Add(new PackScanResult(pack.Code, state, missing));
        }

        return results;
    }
}
