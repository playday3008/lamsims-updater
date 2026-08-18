using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
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

    /// <summary>
    /// Reads the ContentPresenter, not the ListBoxItem. Fluent's ListBoxItem control theme paints
    /// its selected and pointer-over backgrounds onto PART_ContentPresenter through its own
    /// pseudo-class styles, which outrank the TemplateBinding carrying the item's Background down.
    /// The earlier form of this test asserted ListBoxItem.Background and passed while the presenter
    /// painted Fluent's #0078d7; it read the property that was SET rather than the one that PAINTS.
    ///
    /// Pointer-over gets no companion test. Driving :pointerover headlessly means synthesising a
    /// pointer at a position the item occupies, and this host does not lay out or hit-test to a
    /// point; a test that toggled the pseudo-class by hand would assert Avalonia's styling engine
    /// rather than that the pointer ever reaches the row. The selector is the same shape as the
    /// selected one and is covered by SelectorSweepTests' name and pseudo-class check only.
    /// </summary>
    [AvaloniaFact]
    public void A_selected_row_takes_the_selection_surface()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"), Packs.Entry("EP02", "Get Together"));

        var listBox = ViewHost.Find<ListBox>(host.Window);
        listBox.SelectedIndex = 0;

        var selected = SelectionSurface(host.RowVisual("EP01"));
        var other = SelectionSurface(host.RowVisual("EP02"));

        // The pair matters: asserting only that the selected row is Surface1 would pass if every
        // row were Surface1, which is not a selection indicator at all.
        Assert.Equal(Color.Parse("#45475a"),
            Assert.IsAssignableFrom<ISolidColorBrush>(selected.Background!).Color);
        Assert.NotEqual(Color.Parse("#45475a"),
            Assert.IsAssignableFrom<ISolidColorBrush>(other.Background!).Color);
    }

    /// <summary>
    /// The row's own template presenter. Matching on the name alone is not enough: the CheckBox
    /// nested in the row template has a PART_ContentPresenter of its own, so the templated parent
    /// is what disambiguates.
    /// </summary>
    private static ContentPresenter SelectionSurface(ListBoxItem row) =>
        ViewHost.Find<ContentPresenter>(
            row, p => p.Name == "PART_ContentPresenter" && ReferenceEquals(p.TemplatedParent, row));

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

    /// <summary>
    /// Fluent resolves the slider's track fill and thumb from its accent, which is Windows blue.
    /// The two Slider* keys this phase originally shipped were both the container backdrop, which
    /// is Transparent and changes nothing a user sees.
    /// </summary>
    [AvaloniaFact]
    public void The_connections_slider_is_mauve_rather_than_fluents_accent()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"));

        // The Slider is inside the collapsed Advanced Expander, which realizes no content until
        // it is opened; without this the search below finds nothing.
        ViewHost.Find<Expander>(host.Window).IsExpanded = true;
        host.Pump();

        var slider = ViewHost.Find<Slider>(host.Window);
        var thumb = ViewHost.Find<Thumb>(slider);
        var filled = ViewHost.Find<RepeatButton>(slider, b => b.Name == "PART_DecreaseButton");
        var rest = ViewHost.Find<RepeatButton>(slider, b => b.Name == "PART_IncreaseButton");

        Assert.Equal(Color.Parse("#cba6f7"),
            Assert.IsAssignableFrom<ISolidColorBrush>(thumb.Background!).Color);
        Assert.Equal(Color.Parse("#cba6f7"),
            Assert.IsAssignableFrom<ISolidColorBrush>(filled.Background!).Color);

        // The pair matters: a track painted mauve end to end indicates no value at all, and would
        // satisfy a lone "the track is mauve" assertion.
        Assert.Equal(Color.Parse("#45475a"),
            Assert.IsAssignableFrom<ISolidColorBrush>(rest.Background!).Color);
    }

    /// <summary>
    /// The checked mark is GreenBrush #a6e3a1: upstream stroked both the
    /// tick and the checked border. A GreenBrush defined and referenced by nothing leaves the
    /// checked box painted in Fluent's accent blue.
    /// </summary>
    [AvaloniaFact]
    public void A_checked_box_is_marked_green()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"), Packs.Entry("EP02", "Get Together"));

        host.Row("EP01").IsChecked = true;

        var ticked = ViewHost.Find<CheckBox>(host.RowVisual("EP01"));
        var blank = ViewHost.Find<CheckBox>(host.RowVisual("EP02"));

        Assert.True(ticked.IsChecked);
        Assert.False(blank.IsChecked);

        var glyph = ViewHost.Find<Avalonia.Controls.Shapes.Path>(ticked, p => p.Name == "CheckGlyph");
        var border = ViewHost.Find<Border>(ticked, b => b.Name == "PART_Border");

        Assert.Equal(Color.Parse("#a6e3a1"),
            Assert.IsAssignableFrom<ISolidColorBrush>(glyph.Fill!).Color);
        Assert.Equal(Color.Parse("#a6e3a1"),
            Assert.IsAssignableFrom<ISolidColorBrush>(border.BorderBrush!).Color);

        // The pair again: a border green in both states marks nothing.
        Assert.NotEqual(Color.Parse("#a6e3a1"),
            Assert.IsAssignableFrom<ISolidColorBrush>(
                ViewHost.Find<Border>(blank, b => b.Name == "PART_Border").BorderBrush!).Color);
    }
}
