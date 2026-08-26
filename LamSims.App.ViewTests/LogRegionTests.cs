using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Xunit;
using LamSims.Core.Logging;

namespace LamSims.App.ViewTests;

public class LogRegionTests
{
    [AvaloniaFact]
    public void Each_written_line_renders_its_time_and_its_text()
    {
        using var host = ViewHost.Show(out var services);

        services.Log.Write(LogLine.Info("Fetching https://host.example.invalid/EP01.zip", "EP01"));
        host.Pump();

        var texts = host.Window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text).ToList();

        Assert.Contains(texts, t => t is not null && t.Contains("Fetching https://host.example.invalid"));
        Assert.Contains("EP01", texts);
    }

    /// <summary>
    /// The severity classes are the only thing that colours a line, and ThemeSweepTests only
    /// proves they are styled — not that they are ever put on an element at runtime.
    /// </summary>
    [AvaloniaFact]
    public void A_warning_line_carries_the_warning_class()
    {
        using var host = ViewHost.Show(out var services);

        services.Log.Write(LogLine.Warning("careful", "EP01"));
        host.Pump();

        Assert.Contains(host.Window.GetVisualDescendants().OfType<TextBlock>(),
                        t => t.Classes.Contains("log-warning"));
    }

    /// <summary>
    /// Each line's item template renders three "log-line" TextBlocks (Time, Code, Text), so a
    /// non-virtualizing StackPanel — the ItemsControl default when no ItemsPanel is set — would
    /// realize all 500 * 3 = 1500 of them regardless of what the window can actually show. A
    /// VirtualizingStackPanel instead realizes only what the viewport (plus a small overscan
    /// buffer) needs, which stays a small, roughly constant number as the line count grows. The
    /// threshold below sits far under 1500 specifically so this discriminates the two: it fails
    /// against the unvirtualized default and passes against VirtualizingStackPanel.
    /// </summary>
    [AvaloniaFact]
    public void Only_a_small_window_of_lines_is_realized_once_the_log_outgrows_the_viewport()
    {
        using var host = ViewHost.Show(out var services);

        for (var i = 0; i < 500; i++) services.Log.Write(LogLine.Info($"log line {i}"));
        host.Pump();

        var realized = host.Window.GetVisualDescendants().OfType<TextBlock>()
            .Count(t => t.Classes.Contains("log-line"));

        Assert.True(realized > 0, "no log lines were realized at all, so this proves nothing");
        Assert.True(realized < 600,
            $"expected far fewer than 1500 realized log TextBlocks with virtualization on, got {realized}");
    }
}
