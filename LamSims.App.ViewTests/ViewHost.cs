using LamSims.Core.Catalogs;
using LamSims.Core.Queueing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LamSims.App.Services;
using LamSims.App.ViewModels;
using LamSims.Core.Installing;
using LamSims.Core.Settings;
using LamSims.Core.Unlocking;

namespace LamSims.App.ViewTests;

public sealed class ViewHost : IDisposable
{
    public required string Root { get; init; }
    public required MainViewModel ViewModel { get; init; }
    public required MainWindow Window { get; init; }

    /// <summary>The recording queue behind the rows, for tests that invoke a control.</summary>
    public required StubQueue Queue { get; init; }

    public static ViewHost Show(params PackEntry[] packs) =>
        Show(packs, unlockerService: null, unlockerHost: null, unlockerAssets: null);

    /// <summary>
    /// The three parameters default to the same inert values <see cref="Services"/> always used,
    /// so this changes no existing call site. A distinct overload rather than trailing optional
    /// parameters on the one above, because <c>params PackEntry[] packs</c> must be the last
    /// parameter in its own signature.
    /// </summary>
    public static ViewHost Show(PackEntry[] packs, UnlockerService? unlockerService,
        IUnlockerHost? unlockerHost, IUnlockerAssetSource? unlockerAssets)
    {
        var root = Directory.CreateTempSubdirectory("lamsims-view").FullName;

        var paths = new AppPaths(Path.Combine(root, "config"));
        paths.EnsureCreated();

        var (services, queue) = Services(paths, unlockerService, unlockerHost, unlockerAssets);

        var viewModel = new MainViewModel(services, save: (_, _) => Task.CompletedTask);
        viewModel.BuildRows(packs);

        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        return new ViewHost { Root = root, ViewModel = viewModel, Window = window, Queue = queue };
    }

    /// <summary>
    /// The service graph both window constructors get. The CatalogLoader's HttpClient cannot leave
    /// the machine: the shipped constructor DOES run OnOpened's StartAsync, which resolves a
    /// catalog, so a loader over a live handler would put a unit test on the public internet.
    /// </summary>
    public static (AppServices Services, StubQueue Queue) Services(AppPaths paths,
        UnlockerService? unlockerService = null, IUnlockerHost? unlockerHost = null,
        IUnlockerAssetSource? unlockerAssets = null)
    {
        var queue = new StubQueue();

        return (new AppServices(
            paths,
            new SettingsStore(paths),
            new AppSettings(),
            null,
            new CatalogLoader(new HttpClient(new OfflineHandler()), paths),
            new InstallStateStore(paths.InstallStateDirectory),
            queue,
            new StubDispatcher(),
            new StubPickers(),
            new StubClock(),
            null,
            // No backend registered by default, so IsSupported is false and the region hides
            // itself. A test that wants the region visible supplies its own recording backend.
            unlockerService ?? new UnlockerService([]),
            unlockerHost ?? new FakeUnlockerHost { IsAvailable = false },
            unlockerAssets ?? new StubUnlockerAssets()), queue);
    }

    /// <summary>
    /// Runs dispatcher jobs until a condition holds. Headless has no message loop of its own, and
    /// the shipped window's close is deliberately deferred behind an await, so nothing completes
    /// without this. Bounded rather than timed: it never waits on the clock for a result.
    ///
    /// Yields per pass rather than spinning. This is also used to wait on work that runs on a
    /// thread-pool thread (UnlockerViewModel's detection hops off the UI thread via Task.Run), and
    /// RunJobs() on an empty queue returns almost immediately — a pure spin can burn all 20000
    /// passes in a few milliseconds, well under thread-pool scheduling latency under load, before
    /// the pool thread has even been scheduled to run the work whose completion this is waiting
    /// for. Thread.Yield() gives that thread a chance to actually run between passes instead of
    /// starving it. Not a timed wait: the iteration cap is still the only hang guard, and no clock
    /// is read anywhere in this method.
    /// </summary>
    public static bool PumpUntil(Func<bool> condition, int passes = 20000)
    {
        for (var i = 0; i < passes; i++)
        {
            if (condition()) return true;

            Dispatcher.UIThread.RunJobs();
            Thread.Yield();
        }

        return condition();
    }

    public PackRowViewModel Row(string code) => ViewModel.RowFor(code)!;

    /// <summary>
    /// Locates a row by its data context. Never locate a row by searching for a control containing
    /// its text: an outer Grid contains every row's text, so such a search silently returns the
    /// FIRST row's controls for any code asked for.
    /// </summary>
    public ListBoxItem RowVisual(string code)
    {
        var row = Row(code);

        return Window.GetVisualDescendants().OfType<ListBoxItem>()
            .First(i => ReferenceEquals(i.DataContext, row));
    }

    public static T Find<T>(Visual root, Func<T, bool> match) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().First(match);

    public static T Find<T>(Visual root) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().First();

    /// <summary>
    /// Runs the layout pass that a real message loop would. Headless does not pump on its own, and
    /// an item added to a bound collection AFTER Show() gets its container but not its template
    /// content until layout runs, so a test that adds a banner and looks for its Border finds
    /// nothing without this. Property changes on already-realized controls need no pump.
    /// </summary>
    public void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Window.UpdateLayout();
    }

    public void Dispose()
    {
        Window.Close();
        Directory.Delete(Root, recursive: true);
    }
}

/// <summary>
/// Records what reached the queue. Empty bodies would let a control bound to the wrong method pass
/// every rendering test: the binding resolves, the command runs, and nothing observes which one.
/// </summary>
public sealed class StubQueue : IQueueController
{
    private readonly List<string> _calls = new();

    /// <summary>RunAsync arrives from the pool, so this has more than one writer.</summary>
    public IReadOnlyList<string> Calls
    {
        get { lock (_calls) return _calls.ToArray(); }
    }

    private void Add(string call)
    {
        lock (_calls) _calls.Add(call);
    }

    public IAsyncEnumerable<QueueUpdate> Updates => Empty();

    private static async IAsyncEnumerable<QueueUpdate> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes when the queue is stopped, as the real one does: <c>PackQueue.RunAsync</c> returns
    /// from its finally once cancellation has been honoured. A task that never completed at all
    /// would hang <c>ShutdownAsync</c>, which awaits it, and with it the window's deferred close.
    /// </summary>
    public Task RunAsync(CancellationToken ct)
    {
        Add("Run");
        ct.Register(() => _stopped.TrySetResult());

        return _stopped.Task;
    }

    public void Enqueue(PackEntry pack, string gameDirectory) => Add($"Enqueue:{pack.Code}");

    public void Cancel(string code) => Add($"Cancel:{code}");

    public bool Remove(string code)
    {
        Add($"Remove:{code}");
        return true;
    }

    public void CancelAll()
    {
        Add("CancelAll");
        _stopped.TrySetResult();
    }

    public void Complete() => Add("Complete");

    public void Pause() => Add("Pause");

    public void Resume() => Add("Resume");

    /// <summary>
    /// Set to keep a shutdown in flight, the way a real multi-gigabyte extract does. Without it a
    /// stubbed shutdown finishes before a second close can arrive, so a test about what happens
    /// DURING shutdown never reaches the state it names.
    /// </summary>
    public TaskCompletionSource? HoldDispose { get; set; }

    public async ValueTask DisposeAsync()
    {
        Add("Dispose");

        if (HoldDispose is not null) await HoldDispose.Task;

        _stopped.TrySetResult();
    }
}

/// <summary>Makes a view test structurally unable to reach the network.</summary>
public sealed class OfflineHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new HttpRequestException($"A view test must not reach the network ({request.RequestUri}).");
}

public sealed class StubDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

public sealed class StubPickers : IPickerService
{
    public Task<string?> PickFolderAsync(string title, string? startAt) => Task.FromResult<string?>(null);

    public Task<string?> PickFileAsync(string title, string? startAt) => Task.FromResult<string?>(null);
}

public sealed class StubClock : IClock
{
    public DateTimeOffset UtcNow => new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
}

public static class Packs
{
    public static PackEntry Entry(
        string code = "EP01",
        string name = "Get to Work",
        long size = 12_400_000_000) =>
        new(code, name, PackType.Expansion, size, null, new string('a', 64),
            [new Uri("https://example.invalid/pack.zip")], [code]);
}
