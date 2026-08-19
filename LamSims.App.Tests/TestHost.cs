using LamSims.App;
using LamSims.App.Services;
using LamSims.App.ViewModels;
using LamSims.Core.Installing;
using LamSims.Core.Settings;
using LamSims.Core.Unlocking;

namespace LamSims.App.Tests;

public sealed class TestHost : IDisposable
{
    public required string Root { get; init; }
    public required string GameDirectory { get; init; }
    public required AppPaths Paths { get; init; }
    public required IQueueController Controller { get; init; }
    public required FakePickers Pickers { get; init; }
    public required FakeClock Clock { get; init; }
    public required List<AppSettings> Saved { get; init; }

    /// <summary>
    /// The recording queue, for the tests that assert its call sequence. A test that passed its
    /// own <c>queue</c> has nothing to record and must not ask: the cast throws rather than
    /// silently handing back a queue the view model never touched.
    /// </summary>
    public RecordingQueue Queue => (RecordingQueue)Controller;

    public static MainViewModel ViewModel(
        out TestHost host,
        Func<CancellationToken, Task<CatalogResolution>>? resolve = null,
        Func<CatalogSource, CancellationToken, Task<CatalogResolution>>? load = null,
        Func<AppSettings, CancellationToken, Task>? save = null,
        string? settingsError = null,
        Action<AppSettings>? seed = null,
        IQueueController? queue = null,
        UnlockerService? unlockerService = null,
        IUnlockerHost? unlockerHost = null,
        IUnlockerAssetSource? unlockerAssets = null)
    {
        var root = Directory.CreateTempSubdirectory("lamsims-vm").FullName;
        var game = Path.Combine(root, "game");
        Directory.CreateDirectory(game);

        var paths = new AppPaths(Path.Combine(root, "config"));
        paths.EnsureCreated();

        var controller = queue ?? new RecordingQueue();
        var pickers = new FakePickers();
        var clock = new FakeClock();
        var saved = new List<AppSettings>();

        // Seeded before the view model is constructed: its constructor copies these into the
        // backing fields directly, so a change made afterwards would never be read.
        var settings = new AppSettings();
        seed?.Invoke(settings);

        var services = new AppServices(
            paths,
            new SettingsStore(paths),
            settings,
            settingsError,
            new CatalogLoader(new HttpClient(), paths),
            new InstallStateStore(paths.InstallStateDirectory),
            controller,
            new ImmediateDispatcher(),
            pickers,
            clock,
            null,
            // A service with no backends reports IsSupported false, so the region hides itself
            // and every pre-existing test is unaffected by this phase. A wiring test overrides
            // one or all three to drive UnlockerViewModel through a real MainViewModel.
            unlockerService ?? new UnlockerService([]),
            unlockerHost ?? new FakeUnlockerHost { IsAvailable = false },
            unlockerAssets ?? new StubUnlockerAssets());

        host = new TestHost
        {
            Root = root,
            GameDirectory = game,
            Paths = paths,
            Controller = controller,
            Pickers = pickers,
            Clock = clock,
            Saved = saved,
        };

        var recordingSave = save ?? ((settings, _) =>
        {
            saved.Add(settings);
            return Task.CompletedTask;
        });

        return new MainViewModel(services, resolve, load, recordingSave);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
}

public sealed class FakePickers : IPickerService
{
    public string? NextFolder { get; set; }
    public string? NextFile { get; set; }

    public Task<string?> PickFolderAsync(string title, string? startAt) => Task.FromResult(NextFolder);

    public Task<string?> PickFileAsync(string title, string? startAt) => Task.FromResult(NextFile);
}
