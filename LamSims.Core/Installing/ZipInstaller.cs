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

public sealed record InstallResult(InstallOutcome Outcome, int EntriesWritten, string? Error)
{
    public static InstallResult Installed(int entriesWritten) =>
        new(InstallOutcome.Installed, entriesWritten, null);

    public static InstallResult InsufficientSpace(string error) =>
        new(InstallOutcome.InsufficientSpace, 0, error);

    public static InstallResult Cancelled(int entriesWritten) =>
        new(InstallOutcome.Cancelled, entriesWritten, null);

    public static InstallResult Failed(string error, int entriesWritten = 0) =>
        new(InstallOutcome.Failed, entriesWritten, error);
}

public sealed record InstallProgress(string Code, long BytesWritten, long TotalBytes, string CurrentEntry);

/// <summary>
/// Streams archive entries straight into the game directory. Upstream extracted to a
/// temporary directory and then copied, needing roughly three times the pack size free;
/// this needs one.
///
/// The zip-slip guard below is lexical: it resolves each entry's destination with
/// <see cref="Path.GetFullPath(string)"/> and checks the result falls under the game
/// directory, but that call does not resolve symlinks. A symlink already present inside the
/// game directory and pointing outside it would pass the prefix check unchanged, and the
/// entry would be written through it. This class defends against a hostile archive, not
/// against a game directory that already contains a hostile symlink.
/// </summary>
public sealed class ZipInstaller
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>
    /// Reports the caller's cancellation through the result's <c>Cancelled</c> outcome
    /// rather than throwing <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<InstallResult> InstallAsync(
        PackEntry pack, string archivePath, string gameDirectory,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var written = 0;

        try
        {
            Directory.CreateDirectory(gameDirectory);
            DiskSpace.EnsureAvailable(gameDirectory, pack.RequiredInstallBytes);

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));

            using var archive = ZipFile.OpenRead(archivePath);

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
                    && !destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    throw new ZipSlipException(entry.FullName);
                }

                planned.Add((entry, destination));
                totalBytes += entry.Length;
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
                    // Upstream caught everything and reported one generic message. Naming the
                    // entry is the difference between a bug report and a guess, whether the
                    // failure was creating its parent directory or writing its bytes.
                    throw new IOException(
                        $"Entry '{entry.FullName}' could not be written to '{destination}': {e.Message}", e);
                }

                written++;
                bytes += entry.Length;
                Report(progress, new InstallProgress(pack.Code, bytes, totalBytes, entry.FullName));
            }

            // Deleted only now. A failed or cancelled install keeps the verified archive so
            // the retry needs no new download.
            File.Delete(archivePath);
            return InstallResult.Installed(written);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return InstallResult.Cancelled(written);
        }
        catch (InsufficientDiskSpaceException e)
        {
            return InstallResult.InsufficientSpace(e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return InstallResult.Failed(e.Message, written);
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
