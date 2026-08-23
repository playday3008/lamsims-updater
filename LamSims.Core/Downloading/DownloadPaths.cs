using System;
using System.IO;
using LamSims.Core;

namespace LamSims.Core.Downloading;

/// <summary>
/// Resolves where partial downloads, archives and quarantined files live.
/// Deliberately not the system temp directory: on most Linux distributions /tmp is
/// tmpfs, i.e. RAM, and archives here reach several gigabytes.
/// </summary>
public sealed class DownloadPaths
{
    /// <summary>
    /// Where downloads go when nothing is configured. <see cref="Retarget"/> returns here when it
    /// is given nothing, so clearing the setting cannot leave the engine on the directory that
    /// was just cleared.
    /// </summary>
    public string DefaultRoot { get; }

    public string Root { get; private set; }

    public DownloadPaths(string? overrideRoot = null)
    {
        DefaultRoot = overrideRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "lamsims-updater", "downloads");
        Root = DefaultRoot;
    }

    /// <summary>
    /// Moves every path this instance hands out to <paramref name="directory"/>, or back to
    /// <see cref="DefaultRoot"/> when it is null or blank, and creates it. Every consumer holds
    /// this one instance and asks it for paths per call, so one call moves them all and the
    /// setting can be applied without rebuilding the object graph.
    ///
    /// <para><b>Only while nothing is running.</b> A retarget under a live download orphans the
    /// <c>.part</c> file and the lock the run is still holding, and the next pass would see
    /// neither. The application gates the call on an empty queue.</para>
    ///
    /// <para>Existing archives are not moved. <see cref="Root"/> is assigned only once the
    /// directory exists, so a directory that cannot be created throws and leaves the engine
    /// pointed where it already was.</para>
    /// </summary>
    public void Retarget(string? directory)
    {
        var root = string.IsNullOrWhiteSpace(directory) ? DefaultRoot : directory;
        Directory.CreateDirectory(root);
        Root = root;
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
        if (FileNameRules.HasInvalidCharacter(code))
            throw new ArgumentException($"Pack code '{code}' contains path characters.", nameof(code));
        // "." and ".." carry no invalid filename character, so the check above lets them through
        // and Path.Combine resolves them to Root itself or to its parent.
        if (code is "." or "..")
            throw new ArgumentException($"Pack code '{code}' is a directory reference.", nameof(code));
        if (FileNameRules.IsReservedDeviceName(code))
            throw new ArgumentException(
                $"Pack code '{code}' is a reserved device name.", nameof(code));
        if (FileNameRules.HasTrailingDotOrSpace(code))
            throw new ArgumentException(
                $"Pack code '{code}' ends with a dot or a space.", nameof(code));
        return code;
    }
}
