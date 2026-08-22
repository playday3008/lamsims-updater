using LamSims.Core.Scanning;
using System.Linq;
using Xunit;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace LamSims.App.ViewTests;

public class RowLayoutTests
{
    /// <summary>
    /// SizeText was computed and unit-tested but bound nowhere, so the size column did not exist
    /// in the shipped application.
    /// </summary>
    [AvaloniaFact]
    public void A_row_shows_the_pack_size()
    {
        using var host = ViewHost.Show(Packs.Entry("EP03", "City Living", 12_400_000_000));

        var row = host.RowVisual("EP03");
        var expected = host.Row("EP03").SizeText;

        Assert.Equal("12.4 GB", expected);
        Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == expected);
    }

    [AvaloniaFact]
    public void The_size_column_is_the_secondary_colour()
    {
        using var host = ViewHost.Show(Packs.Entry("EP03", "City Living", 12_400_000_000));

        var size = ViewHost.Find<TextBlock>(host.RowVisual("EP03"), t => t.Text == "12.4 GB");

        Assert.Equal(Color.Parse("#a6adc8"),
            Assert.IsAssignableFrom<ISolidColorBrush>(size.Foreground).Color);
    }

    /// <summary>
    /// Covers IsCheckable, the binding and the rendered control together. An unscanned row's box
    /// must still be enabled, or "disabled" would be indistinguishable from "every box is
    /// disabled".
    /// </summary>
    [AvaloniaFact]
    public void An_installed_rows_checkbox_is_disabled_and_an_unscanned_one_is_not()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"), Packs.Entry("EP02", "Get Together"));

        host.Row("EP01").ApplyScan(new PackScanResult("EP01", PackInstallState.Installed, [], null, null));

        Assert.False(host.Row("EP01").IsCheckable);
        Assert.True(host.Row("EP02").IsCheckable);

        Assert.False(ViewHost.Find<CheckBox>(host.RowVisual("EP01")).IsEnabled);
        Assert.True(ViewHost.Find<CheckBox>(host.RowVisual("EP02")).IsEnabled);
    }
}
