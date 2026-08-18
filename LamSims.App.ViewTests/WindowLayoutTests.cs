using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace LamSims.App.ViewTests;

public class WindowLayoutTests
{
    [AvaloniaFact]
    public void The_window_has_a_resize_floor()
    {
        using var host = ViewHost.Show(Packs.Entry());

        // Star columns with no floor collapse into illegibility; a window that has never been
        // given a minimum can be dragged to nothing.
        Assert.Equal(760d, host.Window.MinWidth);
        Assert.Equal(480d, host.Window.MinHeight);
    }

    [AvaloniaFact]
    public void No_text_in_the_window_is_pinned_to_a_fixed_width()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"), Packs.Entry("EP02", "Get Together"));

        // A collapsed Expander realizes no content, so without this the Advanced region (the
        // downloads and connections rows) is silently skipped and this test's universal claim
        // is false.
        ViewHost.Find<Expander>(host.Window).IsExpanded = true;
        host.Pump();

        // Control.Width is NaN unless explicitly set, so this reads the rendered objects rather
        // than the markup, and covers the row template as well as the chrome.
        var pinned = host.Window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => !double.IsNaN(t.Width))
            .Select(t => $"{t.Text}: Width={t.Width}")
            .ToList();

        Assert.True(pinned.Count == 0, string.Join(Environment.NewLine, pinned));
    }

    [AvaloniaFact]
    public void The_row_columns_are_the_shape_the_spec_gives()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"));

        var grid = ViewHost.Find<Grid>(host.RowVisual("EP01"), g => g.ColumnDefinitions.Count == 6);

        Assert.Equal(
            ["Auto", "Auto", "2*", "3*", "Auto", "Auto"],
            grid.ColumnDefinitions.Select(c => c.Width.ToString()).ToArray());
    }

    [AvaloniaFact]
    public void Shutting_down_makes_the_content_inert_but_leaves_the_notice_readable()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"));

        var notice = ViewHost.Find<TextBlock>(host.Window,
            t => t.Text is not null && t.Text.StartsWith("Finishing up", StringComparison.Ordinal));

        Assert.False(notice.IsVisible);
        Assert.True(ViewHost.Find<Button>(host.Window).IsEffectivelyEnabled);

        host.ViewModel.IsShuttingDown = true;

        Assert.True(notice.IsVisible);
        Assert.True(notice.IsEffectivelyEnabled);
        Assert.False(ViewHost.Find<Button>(host.Window).IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void The_empty_state_shows_only_when_there_is_something_to_say()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"));

        // Located by name rather than by predicate. Now that the window is themed, a Button's
        // content presenter also renders a TextBlock inheriting MainViewModel as its DataContext,
        // and a centre-alignment predicate can match that instead, asserting the wrong control
        // while reading as a pass.
        var empty = host.Window.FindControl<TextBlock>("EmptyState")!;

        Assert.NotNull(empty);
        Assert.False(empty.IsVisible);

        host.ViewModel.EmptyStateMessage = "This catalog lists no packs.";

        Assert.True(empty.IsVisible);
        Assert.Equal("This catalog lists no packs.", empty.Text);
    }
}
