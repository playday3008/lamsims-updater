using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Compression;
using LamSims.Core.Catalogs;
using LamSims.Core.Downloading;

namespace LamSims.Core.Installing;

public sealed class ZipSlipException : IOException
{
    public ZipSlipException(string entryName)
        : base($"Archive entry '{entryName}' resolves outside the game directory.")
    {
        EntryName = entryName;
    }

    public string EntryName { get; }
}

public enum InstallOutcome { Installed, InsufficientSpace, Cancelled, Failed }

/// <summary>
/// The outcome of an install. <see cref="Warnings"/> carries what went wrong without failing
/// the install, such as the install journal not being updatable. It is never null, so a caller
/// can bind it directly.
/// </summary>
public sealed record InstallResult(
    InstallOutcome Outcome, int EntriesWritten, string? Error, IReadOnlyList<string> Warnings)
{
    private static readonly IReadOnlyList<string> None = Array.Empty<string>();

    public static InstallResult Installed(int entriesWritten, IReadOnlyList<string>? warnings = null) =>
        new(InstallOutcome.Installed, entriesWritten, null, warnings ?? None);

    public static InstallResult InsufficientSpace(string error) =>
        new(InstallOutcome.InsufficientSpace, 0, error, None);

    public static InstallResult Cancelled(int entriesWritten) =>
        new(InstallOutcome.Cancelled, entriesWritten, null, None);

    public static InstallResult Failed(string error, int entriesWritten = 0) =>
        new(InstallOutcome.Failed, entriesWritten, error, None);
}

public sealed record InstallProgress(string Code, long BytesWritten, long TotalBytes, string CurrentEntry);

/// <summary>
/// Streams archive entries straight into the game directory, so the install needs the pack's
/// size free rather than roughly three times it.
///
/// The zip-slip guard below is lexical: it resolves each entry's destination with
/// <see cref="Path.GetFullPath(string)"/> and checks the result falls under the game
/// directory, but that call does not resolve symlinks. A symlink already present inside the
/// game directory and pointing outside it would pass the prefix check unchanged, and the
/// entry would be written through it. This class defends against a hostile archive, not
/// against a game directory that already contains a hostile symlink.
///
/// Extraction is journalled: a marker recording this pack and this game directory is written
/// durably before the first entry and rewritten as complete after the last one. A marker still
/// reading "installing" is what makes an interrupted install visible to the scanner, since
/// directory presence alone cannot distinguish one from a complete install: a cancelled
/// extraction leaves the directory it had already started filling.
///
/// A failed or cancelled install cleans up neither the marker nor the files. The marker is the
/// record of the interruption, and deleting what this install wrote is not safe: entries
/// overwrite existing files, including files in shared roots such as Delta/ that other packs
/// also own, so a rollback without pre-images would destroy another pack's current state.
/// </summary>
public sealed class ZipInstaller
{
    private const int BufferSize = 1024 * 1024;

    private readonly InstallStateStore _state;
    private readonly Func<string, long> _availableBytes;
    private readonly Action<InstallProgress>? _entryWritten;

    /// <param name="availableBytes">
    /// Free bytes on the volume holding a path. Defaults to the real measurement; a test supplies
    /// its own, because the sizes that make the space checks fire cannot be produced on a real
    /// volume.
    /// </param>
    /// <param name="entryWritten">
    /// Called on the extracting thread as each entry lands, beside the progress report and before
    /// the next entry's cancellation check. A test that has to act while an extraction is still
    /// running uses it, so that acting is ordered against the extract rather than racing a write.
    /// </param>
    public ZipInstaller(
        InstallStateStore state,
        Func<string, long>? availableBytes = null,
        Action<InstallProgress>? entryWritten = null)
    {
        _state = state;
        _availableBytes = availableBytes ?? DiskSpace.GetAvailableBytes;
        _entryWritten = entryWritten;
    }

    private void EnsureSpace(string path, long requiredBytes)
    {
        var available = _availableBytes(path);
        if (available < requiredBytes)
            throw new InsufficientDiskSpaceException(path, requiredBytes, available);
    }

    /// <summary>
    /// Reports the caller's cancellation through the result's <c>Cancelled</c> outcome
    /// rather than throwing <see cref="OperationCanceledException"/>.
    ///
    /// The caller must already have verified that <paramref name="archivePath"/> hashes to
    /// <paramref name="pack"/>'s <see cref="PackEntry.Sha256"/>: the marker this method writes
    /// records that digest as the identity of what was installed, without re-checking it
    /// against the archive's actual bytes.
    /// </summary>
    public async Task<InstallResult> InstallAsync(
        PackEntry pack, string archivePath, string gameDirectory,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var written = 0;

        if (string.IsNullOrWhiteSpace(gameDirectory))
            return InstallResult.Failed("The 'gameDirectory' argument must not be blank.");

        // The game directory is where The Sims 4 is already installed, so it exists by the
        // time anything is installed into it. Creating whatever path was supplied would
        // silently materialise a typo and extract gigabytes into it, and the scanner already
        // reads a missing game directory as an error rather than as something to create.
        if (!Directory.Exists(gameDirectory))
            return InstallResult.Failed($"The game directory '{gameDirectory}' does not exist.");

        try
        {
            EnsureSpace(gameDirectory, pack.RequiredInstallBytes);

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));

            // A filesystem or drive root keeps its trailing separator, since
            // TrimEndingDirectorySeparator never strips a root, so appending a second one would
            // give a prefix ('//', 'D:\\') that no destination can start with, and every entry
            // would read as an escape.
            var inside = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;

            // 'root' rather than the caller's string: the recorded directory is what the
            // scanner compares against, so it is stored already resolved.
            var marker = new InstallMarker(
                InstallMarker.CurrentSchemaVersion, pack.Code, root, pack.Sha256,
                InstallMarkerStatus.Installing, DateTimeOffset.UtcNow);

            // The archive handle is closed before the delete below: it is opened with
            // FileShare.Read and no FileShare.Delete, so on Windows deleting it while this
            // handle is open is a sharing violation and every installed archive would be
            // retained forever.
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                // Every destination is resolved before a single byte is written. A hostile entry
                // found halfway through would already have put files outside the game directory,
                // and resolution reads only the central directory, so it costs nothing.
                var planned = new List<(ZipArchiveEntry Entry, string Destination)>(archive.Entries.Count);
                var totalBytes = 0L;

                foreach (var entry in archive.Entries)
                {
                    string destination;
                    try
                    {
                        destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    }
                    catch (Exception e) when (e is ArgumentException or NotSupportedException)
                    {
                        // A name the path APIs reject is reported the same way a bad write is:
                        // the entry is named rather than the process crashing on a hostile archive.
                        throw new IOException($"Archive entry '{entry.FullName}' has a name that is not a valid path: {e.Message}", e);
                    }

                    if (destination != root
                        && !destination.StartsWith(inside, StringComparison.Ordinal))
                    {
                        throw new ZipSlipException(entry.FullName);
                    }

                    planned.Add((entry, destination));
                    totalBytes += entry.Length;
                }

                // installedSize is optional in the catalog, and RequiredInstallBytes then falls
                // back to the ARCHIVE size, a large underestimate for a compressed pack. The
                // central directory has just given the real figure, and a refusal here still
                // costs nothing: no marker written and no bytes on disk.
                EnsureSpace(gameDirectory, totalBytes);

                // Written after every destination has been resolved and checked, and before a
                // single byte lands: everything that can fail without leaving a trace has
                // already failed, so a refusal here cannot demote a pack that is installed.
                try
                {
                    await _state.SaveAsync(marker, ct);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // Without a journal, a cancel partway through would be indistinguishable
                    // from a complete install for the rest of the installation's life.
                    return InstallResult.Failed(
                        $"The install journal for '{pack.Code}' under '{_state.Root}' could not be written: {e.Message}");
                }

                var bytes = 0L;

                foreach (var (entry, destination) in planned)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        // A directory entry carries a trailing separator and an empty Name.
                        if (entry.Name.Length == 0)
                        {
                            Directory.CreateDirectory(destination);
                            continue;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                        await using var source = entry.Open();
                        await using var target = new FileStream(
                            destination, FileMode.Create, FileAccess.Write, FileShare.None,
                            BufferSize, FileOptions.Asynchronous);

                        await source.CopyToAsync(target, ct);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        // The entry is named in the message, so a report says which one failed
                        // and whether it was creating its parent directory or writing its bytes.
                        throw new IOException(
                            $"Entry '{entry.FullName}' could not be written to '{destination}': {e.Message}", e);
                    }

                    written++;
                    bytes += entry.Length;

                    var landed = new InstallProgress(pack.Code, bytes, totalBytes, entry.FullName);
                    Report(progress, landed);

                    if (_entryWritten is { } entryWritten)
                    {
                        try
                        {
                            entryWritten(landed);
                        }
                        catch (Exception e)
                        {
                            // Named and rethrown as an IOException so it lands in this method's
                            // own filter and comes back as a failed install: unfiltered, it would
                            // escape InstallAsync and break the guarantee that this method always
                            // returns an InstallResult. Report swallows for the same reason and
                            // does not rethrow, because a progress report nobody consumed has no
                            // bearing on whether the pack installed.
                            throw new IOException(
                                $"The entry-written callback failed after '{entry.FullName}': {e.Message}", e);
                        }
                    }
                }
            }

            var warnings = new List<string>();

            try
            {
                // CancellationToken.None: the install has already succeeded, and a racing
                // cancel must not leave the journal claiming the extraction is still running.
                await _state.SaveAsync(
                    marker with { Status = InstallMarkerStatus.Installed, UpdatedUtc = DateTimeOffset.UtcNow },
                    CancellationToken.None);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Not a failed install: every entry is on disk, and reporting Failed would
                // invite the same unbounded retry loop the archive delete below avoids. But the
                // journal now disagrees with the filesystem and the next scan will read this
                // pack as interrupted, so the contradiction is reported rather than swallowed.
                warnings.Add(
                    $"'{pack.Code}' installed, but its journal under '{_state.Root}' could not be updated: {e.Message}. "
                    + "The pack will show as incomplete until it is installed again.");
            }

            // Deleted only now. A failed or cancelled install keeps the verified archive so the
            // retry needs no new download. Every entry is on disk by this point, so a scanner or
            // a second process holding the archive open must not turn a completed install into
            // a reported failure whose retry would re-extract everything and fail the same way.
            // Hence its own try/catch rather than the one below.
            TryDeleteArchive(archivePath);
            return InstallResult.Installed(written, warnings);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return InstallResult.Cancelled(written);
        }
        catch (InsufficientDiskSpaceException e)
        {
            return InstallResult.InsufficientSpace(e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                       or InvalidDataException or ArgumentException)
        {
            // ArgumentException covers the path APIs, which reject a name rather than failing
            // to use it: a UNC game directory on Windows reaches DriveInfo this way. This
            // method always returns an InstallResult.
            return InstallResult.Failed(e.Message, written);
        }
    }

    private static void TryDeleteArchive(string archivePath)
    {
        try
        {
            File.Delete(archivePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Reporting progress must not be able to fail an install that is otherwise fine.</summary>
    private static void Report(IProgress<InstallProgress>? progress, InstallProgress value)
    {
        if (progress is null) return;

        try
        {
            progress.Report(value);
        }
        catch (Exception)
        {
        }
    }
}
