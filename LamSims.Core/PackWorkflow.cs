using LamSims.Core.Catalogs;
using LamSims.Core.Downloading;
using LamSims.Core.Installing;

namespace LamSims.Core;

/// <summary>How far a pack got. <see cref="Done"/> means downloaded, verified and installed.</summary>
public enum PackStage { Downloading, Installing, Done }

public sealed record PackWorkflowResult(
    PackStage ReachedStage,
    DownloadResult? Download,
    InstallResult? Install);

/// <summary>
/// One pack, end to end: fetch it if it is not already here, verify it, install it. The
/// seam the user interface binds to, so a ViewModel sequences nothing itself.
/// </summary>
public sealed class PackWorkflow
{
    private readonly SegmentedDownloader _downloader;
    private readonly ZipInstaller _installer;
    private readonly DownloadPaths _paths;

    public PackWorkflow(SegmentedDownloader downloader, ZipInstaller installer, DownloadPaths paths)
    {
        _downloader = downloader;
        _installer = installer;
        _paths = paths;
    }

    public async Task<PackWorkflowResult> RunAsync(
        PackEntry pack,
        string gameDirectory,
        IProgress<DownloadProgress>? downloadProgress,
        IProgress<InstallProgress>? installProgress,
        CancellationToken ct)
    {
        var archive = _paths.ArchiveFile(pack.Code);
        DownloadResult? download = null;

        // A .zip of exactly the catalogued length is one the engine already verified — it
        // writes that name only after the digest matched. Re-hashing several gigabytes on
        // every retry of a failed install would cost far more than it protects.
        if (!File.Exists(archive) || new FileInfo(archive).Length != pack.Size)
        {
            download = await _downloader.DownloadAsync(pack.ToDownloadRequest(), downloadProgress, ct);

            if (download.Outcome != DownloadOutcome.Completed)
                return new PackWorkflowResult(PackStage.Downloading, download, null);

            archive = download.FilePath!;
        }

        var install = await _installer.InstallAsync(pack, archive, gameDirectory, installProgress, ct);

        return new PackWorkflowResult(
            install.Outcome == InstallOutcome.Installed ? PackStage.Done : PackStage.Installing,
            download,
            install);
    }
}
