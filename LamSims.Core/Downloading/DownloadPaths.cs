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
