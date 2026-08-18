using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using LamSims.App.Services;
using LamSims.App.ViewModels;

namespace LamSims.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel? _viewModel;
    private bool _shutdownStarted;
    private bool _shutdownDone;

    public MainWindow() => AvaloniaXamlLoader.Load(this);

    public MainWindow(AppServices services) : this()
    {
        // The picker is the one service that needs a window handle, which is the whole reason
        // the view model takes an interface rather than calling a dialog itself.
        _viewModel = new MainViewModel(services with { Pickers = new StoragePickerService(this) });
        DataContext = _viewModel;
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (_viewModel is not null) await _viewModel.StartAsync(default);
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_viewModel is null || _shutdownDone)
        {
            base.OnClosing(e);
            return;
        }

        // Shutdown waits for the runner, so the close is deferred rather than blocking the UI
        // thread inside the event.
        e.Cancel = true;
        base.OnClosing(e);

        // A second click on the close button re-enters here while the first ShutdownAsync is
        // still waiting for the installer. ShutdownAsync returns immediately on its own
        // IsShuttingDown guard, so without this guard the second entry would set _shutdownDone
        // and Close() mid-extract, exiting the process with the journal marker open and a
        // Partial install on disk, which is the damage this guard exists to prevent. Only the
        // call that started the shutdown may close the window.
        if (_shutdownStarted) return;

        _shutdownStarted = true;

        await _viewModel.ShutdownAsync();
        _shutdownDone = true;
        Close();
    }
}
