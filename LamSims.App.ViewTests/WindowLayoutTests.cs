using System;
using System.Linq;
using Xunit;
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

    /// <summary>
    /// Covers REALIZED content only. Popup content (the row context menu) and anything inside a
    /// collapsed region is structurally invisible to a visual-tree walk, so this makes its claim
    /// about what the window has actually laid out, not about the markup as a whole.
    /// </summary>
    [AvaloniaFact]
    public void No_realized_text_in_the_window_is_pinned_to_a_fixed_width()
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
            .Select(t => $"realized TextBlock '{t.Text}' is pinned to Width={t.Width}")
            .ToList();

        Assert.True(pinned.Count == 0, string.Join(Environment.NewLine, pinned));
    }

    [AvaloniaFact]
    public void The_row_columns_are_the_shape_the_spec_gives()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"));

        var outer = ViewHost.Find<Grid>(host.RowVisual("EP01"), g => g.RowDefinitions.Count == 2);

        Assert.Equal(
            ["Auto", "1*", "Auto"],
            outer.ColumnDefinitions.Select(c => c.Width.ToString()).ToArray());

        // The second line: code, then the status filling what is left. Only the star column can
        // wrap, which is why the status lives there and the code does not.
        //
        // A direct child, not a descendant search: Fluent's CheckBox template contains a Grid of
        // its own, and a two-column predicate matches that one first.
        var line2 = Assert.Single(outer.Children.OfType<Grid>());

        Assert.Equal(
            ["Auto", "1*"],
            line2.ColumnDefinitions.Select(c => c.Width.ToString()).ToArray());
    }

    /// <summary>
    /// The list is the column that gives way. Its rows trim and wrap; the log's lines are the
    /// text a user is reading while they wait, so the star share goes there.
    /// </summary>
    [AvaloniaFact]
    public void The_log_column_is_twice_the_pack_list()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"));

        var split = ViewHost.Find<Grid>(host.Window,
            g => g.ColumnDefinitions.Count == 3
                 && g.Children.OfType<Border>().Any(b => b.Classes.Contains("log")));

        Assert.Equal(
            ["1*", "8", "2*"],
            split.ColumnDefinitions.Select(c => c.Width.ToString()).ToArray());
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

        // IsVisible says nothing about occlusion, and this test once passed while the label was
        // invisible to users: the ListBox carries an opaque background, a Panel draws in document
        // order, and nothing binds the list's IsVisible, so a label declared before the list is
        // painted and then covered. The assertion is about relative order for that reason.
        var panel = ViewHost.Find<Panel>(host.Window, p => p.Children.Contains(empty));
        var list = ViewHost.Find<ListBox>(host.Window);

        Assert.True(panel.Children.IndexOf(empty) > panel.Children.IndexOf(list),
            "the empty-state label must be declared after the ListBox or the list paints over it");
    }
}
