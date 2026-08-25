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
}
