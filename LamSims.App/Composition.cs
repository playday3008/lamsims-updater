using LamSims.App.Services;
using LamSims.Core;
using LamSims.Core.Catalogs;
using LamSims.Core.Downloading;
using LamSims.Core.Installing;
using LamSims.Core.Queueing;
using LamSims.Core.Settings;

namespace LamSims.App;

public static class Composition
{
    /// <summary>
    /// Built once, from the settings as they are at launch. DownloadDirectory and Connections
    /// are therefore fixed for the session: applying them live would mean rebuilding the queue,
    /// the runner, the client and the bridge.
    /// </summary>
    /// <param name="commandLineCatalog">The catalog named on the command line, if any.</param>
    /// <param name="overrideRoot">
    /// Redirects BOTH roots (the configuration root and, when no download directory is
    /// configured, the download root) so a test cannot write archives, quarantines or lock
    /// files into the developer's real profile. Tests pass one; production passes none.
    /// </param>
    /// <param name="delays">
    /// Exists so an end-to-end test cannot sleep for real time on a retry. Production passes none.
    /// </param>
    public static AppServices Build(
        string? commandLineCatalog, string? overrideRoot = null, IDelayProvider? delays = null)
    {
        var paths = new AppPaths(overrideRoot is null ? null : Path.Combine(overrideRoot, "config"));
        paths.EnsureCreated();

        var settingsStore = new SettingsStore(paths);
        var loaded = settingsStore.Load();
        var settings = loaded.Settings;

        var options = settings.ToDownloadOptions();
        var http = HttpFactory.Create(options);

        var downloadRoot = settings.DownloadDirectory
            ?? (overrideRoot is null ? null : Path.Combine(overrideRoot, "downloads"));

        var downloadPaths = new DownloadPaths(downloadRoot);

        // PackLock does not create the download root and throws DirectoryNotFoundException when
        // it is missing, so it is created here rather than being left to the queue's first run.
        downloadPaths.EnsureCreated();

        var installState = new InstallStateStore(paths.InstallStateDirectory);

        var downloader = new SegmentedDownloader(
            http, downloadPaths, options, RetryOptions.Default, delays ?? new SystemDelayProvider());
        var installer = new ZipInstaller(installState);
        var workflow = new PackWorkflow(downloader, installer, downloadPaths);
        var queue = new PackQueue(workflow, downloadPaths);

        return new AppServices(
            paths,
            settingsStore,
            settings,
            loaded.Error,
            new CatalogLoader(http, paths),
            installState,
            new PackQueueController(queue),
            new AvaloniaUiDispatcher(),
            NullPickers.Instance,      // the window replaces this; it owns the TopLevel
            new SystemClock(),
            commandLineCatalog);
    }
}

/// <summary>Stands in until the window exists; every method reports "the user cancelled".</summary>
internal sealed class NullPickers : IPickerService
{
    public static readonly NullPickers Instance = new();

    public Task<string?> PickFolderAsync(string title, string? startAt) => Task.FromResult<string?>(null);

    public Task<string?> PickFileAsync(string title, string? startAt) => Task.FromResult<string?>(null);
}
