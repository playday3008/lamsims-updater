namespace LamSims.Core.Downloading;

/// <summary>
/// Resolves where partial downloads, archives and quarantined files live.
/// Deliberately not the system temp directory: on most Linux distributions /tmp is
/// tmpfs, i.e. RAM, and archives here reach several gigabytes.
/// </summary>
public sealed class DownloadPaths
{
    public string Root { get; }

    public DownloadPaths(string? overrideRoot = null)
    {
        Root = overrideRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "lamsims-updater", "downloads");
    }

    public string PartFile(string code) => Path.Combine(Root, Validate(code) + ".part");
    public string StateFile(string code) => Path.Combine(Root, Validate(code) + ".part.json");
    public string ArchiveFile(string code) => Path.Combine(Root, Validate(code) + ".zip");
    public string QuarantineFile(string code) => Path.Combine(Root, Validate(code) + ".zip.bad");

    /// <summary>
    /// The verified-identity record for an archive, beside the archive itself. It lives here
    /// rather than in a central store so it follows the archive when the download directory
    /// setting changes or the directory is copied wholesale, and so <see cref="OrphanCleaner"/>
    /// sweeps it with everything else.
    /// </summary>
    public string ArchiveDigestFile(string code) => Path.Combine(Root, Validate(code) + ".zip.json");

    /// <summary>
    /// The cache entry for one unlocker DLL. Through the same <c>Validate</c> guard as every other
    /// member, because <see cref="OrphanCleaner"/> sweeps everything under <see cref="Root"/> and
    /// a path that escaped Root would turn that sweep into a delete somewhere else.
    /// </summary>
    public string UnlockerAssetFile(string fileName) => Path.Combine(Root, Validate(fileName));

    /// <summary>
    /// The advisory lock for one pack, held for its whole run. Beside the archive so a second
    /// instance pointed at the same download directory contends for the same file.
    /// </summary>
    public string LockFile(string code) => Path.Combine(Root, Validate(code) + ".lock");

    public void EnsureCreated() => Directory.CreateDirectory(Root);

    private static string Validate(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Pack code must not be blank.", nameof(code));
        if (code.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"Pack code '{code}' contains path characters.", nameof(code));
        return code;
    }
}
