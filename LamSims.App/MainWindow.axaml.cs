using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using LamSims.App.Services;
using LamSims.App.ViewModels;

namespace LamSims.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel? _viewModel;
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

        await _viewModel.ShutdownAsync();
        _shutdownDone = true;
        Close();
    }
}
