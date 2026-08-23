using Xunit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace LamSims.App.ViewTests;

/// <summary>
/// The catalog box takes a local path or an http(s) URL. Its two halves fail independently: a
/// one-way binding leaves the view model holding the old source while the box shows the new one,
/// and a button wired to the wrong command loads nothing at all.
/// </summary>
public class CatalogSourceTests
{
    private const string Typed = "https://mirror.example.invalid/catalog.json";

    /// <summary>The placeholder tells the catalog box from the search box; nothing else does.</summary>
    private static TextBox CatalogBox(Visual root) =>
        ViewHost.Find<TextBox>(root, t => t.PlaceholderText is not null);

    [AvaloniaFact]
    public void Typing_a_source_into_the_box_reaches_the_view_model()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"));

        CatalogBox(host.Window).Text = Typed;
        host.Pump();

        Assert.Equal(Typed, host.ViewModel.CatalogInput);
    }

    [AvaloniaFact]
    public void Load_is_the_button_that_applies_what_was_typed()
    {
        using var host = ViewHost.Show(Packs.Entry("EP01"));

        var load = ViewHost.Find<Button>(host.Window, b => (b.Content as string) == "Load");

        Assert.Same(host.ViewModel.ApplyCatalogSourceCommand, load.Command);
    }
}
