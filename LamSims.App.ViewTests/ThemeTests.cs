using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using LamSims.Core.Queueing;

namespace LamSims.App.ViewTests;

public class ThemeTests
{
    /// <summary>
    /// FluentTheme's own ButtonBackground is not Catppuccin Surface0, so this
    /// can only pass when Palette.axaml is merged into Application.Resources and wins the lookup.
    /// </summary>
    [AvaloniaFact]
    public void The_palette_overrides_fluents_own_button_background()
    {
        using var host = ViewHost.Show(Packs.Entry());

        var button = ViewHost.Find<Button>(host.Window);
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(button.Background);

        Assert.Equal(Color.Parse("#313244"), brush.Color);
    }

    [AvaloniaFact]
    public void A_disabled_button_is_dimmed()
    {
        using var host = ViewHost.Show(Packs.Entry());

        var button = ViewHost.Find<Button>(host.Window);
        Assert.Equal(1d, button.Opacity);

        button.IsEnabled = false;

        Assert.Equal(0.5d, button.Opacity);
    }

    [AvaloniaFact]
    public void A_selected_row_takes_the_selection_surface()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"), Packs.Entry("EP02", "Get Together"));

        var listBox = ViewHost.Find<ListBox>(host.Window);
        listBox.SelectedIndex = 0;

        var selected = host.RowVisual("EP01");
        var other = host.RowVisual("EP02");

        // The pair matters: asserting only that the selected row is Surface1 would pass if every
        // row were Surface1, which is not a selection indicator at all.
        Assert.Equal(Color.Parse("#45475a"),
            Assert.IsAssignableFrom<ISolidColorBrush>(selected.Background).Color);
        Assert.NotEqual(Color.Parse("#45475a"),
            Assert.IsAssignableFrom<ISolidColorBrush>(other.Background).Color);
    }

    [AvaloniaFact]
    public void The_progress_indicator_is_mauve()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"));

        host.Row("EP01").ApplyQueue(new QueueItemSnapshot(
            "EP01", "Get to Work", QueueItemState.Downloading,
            42, 100, 0, null, null, null, []));

        var bar = ViewHost.Find<ProgressBar>(host.RowVisual("EP01"));

        Assert.True(bar.IsVisible);
        Assert.Equal(Color.Parse("#cba6f7"),
            Assert.IsAssignableFrom<ISolidColorBrush>(bar.Foreground).Color);
    }
}
