using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using LamSims.App.ViewModels;

namespace LamSims.App.ViewTests;

public class BannerTests
{
    private static Border BorderFor(ViewHost host, Banner banner) =>
        host.Window.GetVisualDescendants().OfType<Border>()
            .First(b => ReferenceEquals(b.DataContext, banner));

    /// <summary>
    /// BannerKind has to reach the border it colours, or a settings-load
    /// warning and a dead-queue error are the same grey box.
    /// </summary>
    [AvaloniaFact]
    public void The_three_banner_kinds_are_three_different_borders()
    {
        using var host = ViewHost.Show(Packs.Entry());

        var info = new Banner("a", "a catalog was loaded", BannerKind.Info);
        var warning = new Banner("b", "your settings could not be read", BannerKind.Warning);
        var error = new Banner("c", "the download queue stopped", BannerKind.Error);

        foreach (var banner in (Banner[])[info, warning, error]) host.ViewModel.Banners.Add(banner);

        // Banners are added after Show(), so their templates are not materialized until a layout
        // pass runs. The shipped app's message loop does this automatically; headless does not.
        host.Pump();

        var colours = new[] { info, warning, error }
            .Select(b => Assert.IsAssignableFrom<ISolidColorBrush>(BorderFor(host, b).BorderBrush).Color)
            .ToArray();

        Assert.Equal(3, colours.Distinct().Count());
        Assert.Equal(Color.Parse("#f9e2af"), colours[1]);
        Assert.Equal(Color.Parse("#f38ba8"), colours[2]);
    }
}
