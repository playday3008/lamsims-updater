using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using LamSims.App.Services;
using LamSims.App.ViewModels;
using LamSims.Core.Installing;
using LamSims.Core.Settings;

namespace LamSims.App.ViewTests;

public sealed class ViewHost : IDisposable
{
    public required string Root { get; init; }
    public required MainViewModel ViewModel { get; init; }
    public required MainWindow Window { get; init; }

    public static ViewHost Show(params PackEntry[] packs)
    {
        var root = Directory.CreateTempSubdirectory("lamsims-view").FullName;

        var paths = new AppPaths(Path.Combine(root, "config"));
        paths.EnsureCreated();

        var services = new AppServices(
            paths,
            new SettingsStore(paths),
            new AppSettings(),
            null,
            new CatalogLoader(new HttpClient(), paths),
            new InstallStateStore(paths.InstallStateDirectory),
            new StubQueue(),
            new StubDispatcher(),
            new StubPickers(),
            new StubClock(),
            null);

        var viewModel = new MainViewModel(services, save: (_, _) => Task.CompletedTask);
        viewModel.BuildRows(packs);

        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        return new ViewHost { Root = root, ViewModel = viewModel, Window = window };
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

    public void Dispose()
    {
        Window.Close();
        Directory.Delete(Root, recursive: true);
    }
}

public sealed class StubQueue : IQueueController
{
    public IAsyncEnumerable<QueueUpdate> Updates => Empty();

    private static async IAsyncEnumerable<QueueUpdate> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task RunAsync(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);

    public void Enqueue(PackEntry pack, string gameDirectory) { }

    public void Cancel(string code) { }

    public bool Remove(string code) => true;

    public void CancelAll() { }

    public void Complete() { }

    public void Pause() { }

    public void Resume() { }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
