using System.Linq;
using System.Threading;
using Xunit;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using LamSims.App.ViewModels;
using LamSims.Core.Unlocking;

namespace LamSims.App.ViewTests;

/// <summary>
/// The DLC Unlocker region. Every case below EXECUTES a command and reads what it reached, rather
/// than checking a Command's bound name, which is the only way to catch an
/// InstallSelectedCommand/RemoveSelectedCommand swap.
/// </summary>
public class UnlockerRegionTests
{
    private static UnlockerTarget Target(string path = "/clients/ea", string display = "EA app",
        ClientKind kind = ClientKind.EaApp) =>
        new("test-backend", kind, path, display);

    private static Expander UnlockerExpander(ViewHost host) =>
        ViewHost.Find<Expander>(host.Window, e => e.Header?.ToString() == "DLC Unlocker");

    /// <summary>
    /// Shows the window with a supported backend, runs RefreshAsync so <c>Targets</c> is populated,
    /// then expands the region and pumps.
    ///
    /// An Expander's content is realised LAZILY. Until <c>IsExpanded</c> is set, the content is not
    /// in the visual tree, its bindings have never been applied, and every Command inside reads
    /// null, the same trap RowCommandTests documents for ContextMenu, which needs
    /// <c>menu.Open(owner)</c> before its items can be read.
    /// </summary>
    private static ViewHost ShowExpanded(out RecordingUnlockerBackend backend, params UnlockerTarget[] targets)
    {
        backend = new RecordingUnlockerBackend { Targets = targets };

        var host = ViewHost.Show([], new UnlockerService([backend]), new FakeUnlockerHost(),
            new StubUnlockerAssets());

        // All of DetectTargetsAsync/GetStatusAsync resolve from already-completed tasks, so this
        // finishes synchronously with no dispatcher pump needed.
        host.ViewModel.Unlocker.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();

        UnlockerExpander(host).IsExpanded = true;
        host.Pump();

        return host;
    }

    private static Grid ProgressRow(ViewHost host) =>
        ViewHost.Find<Grid>(host.Window, g => g.Children.OfType<ProgressBar>().Any());

    [AvaloniaFact]
    public void The_region_is_hidden_when_the_service_reports_no_support()
    {
        // The default ViewHost.Show() registers no backend, exactly as every pre-existing test
        // relies on, so IsSupported is false and the region must stay hidden.
        using var host = ViewHost.Show();

        Assert.False(UnlockerExpander(host).IsVisible);
    }

    // The Install button must reach the backend's InstallAsync with this target's own ClientPath.
    // Asserting only that some install ran would also be satisfied by a Command swapped with
    // Remove.
    [AvaloniaFact]
    public void Executing_the_install_button_reaches_the_backend_with_this_targets_path()
    {
        var target = Target("/clients/origin", "Origin", ClientKind.Origin);
        using var host = ShowExpanded(out var backend, target);
        host.ViewModel.Unlocker.Targets[0].IsSelected = true;

        ViewHost.Find<Button>(host.Window, b => b.Content as string == "Install selected").Command!.Execute(null);
        Assert.True(ViewHost.PumpUntil(() => !host.ViewModel.Unlocker.IsBusy),
            "the install never finished: " + string.Join(",", backend.Calls));

        Assert.Contains("Install:/clients/origin", backend.Calls);
        Assert.DoesNotContain("Remove:/clients/origin", backend.Calls);
    }

    // ---- pair: same as above, for Remove. ----
    [AvaloniaFact]
    public void Executing_the_remove_button_reaches_the_backend_with_this_targets_path()
    {
        var target = Target("/clients/origin", "Origin", ClientKind.Origin);
        using var host = ShowExpanded(out var backend, target);
        host.ViewModel.Unlocker.Targets[0].IsSelected = true;

        ViewHost.Find<Button>(host.Window, b => b.Content as string == "Remove selected").Command!.Execute(null);
        Assert.True(ViewHost.PumpUntil(() => !host.ViewModel.Unlocker.IsBusy),
            "the remove never finished: " + string.Join(",", backend.Calls));

        Assert.Contains("Remove:/clients/origin", backend.Calls);
        Assert.DoesNotContain("Install:/clients/origin", backend.Calls);
    }

    // Every other test in this file sets IsSelected/IsBusy on the view model directly and never
    // touches the checkbox itself, so nothing else would notice IsChecked="{Binding IsSelected}"
    // or IsEnabled="{Binding !IsBusy}" being deleted from the template. This drives the CONTROL,
    // both directions, so a deleted binding fails here instead of shipping silently.
    [AvaloniaFact]
    public void The_row_checkbox_binds_its_selection_and_its_enabled_state()
    {
        var target = Target("/clients/origin", "Origin", ClientKind.Origin);
        using var host = ShowExpanded(out _, target);
        var row = host.ViewModel.Unlocker.Targets[0];
        var box = host.Window.GetVisualDescendants().OfType<CheckBox>()
            .First(c => c.DataContext is UnlockerTargetViewModel r && r.ClientPath == target.ClientPath);

        box.IsChecked = true;
        host.Pump();
        Assert.True(row.IsSelected);

        row.IsBusy = true;
        host.Pump();
        Assert.False(box.IsEnabled);
    }

    /// <summary>
    /// Sets the view model's own progress fields directly rather than driving a real operation:
    /// UnlockerViewModelTests already proves RunBatchAsync populates them correctly from a
    /// backend, so this only needs to prove the XAML binds to them.
    /// </summary>
    [AvaloniaFact]
    public void The_progress_line_appears_while_busy_and_shows_the_current_step()
    {
        var target = Target();
        using var host = ShowExpanded(out _, target);
        var vm = host.ViewModel.Unlocker;

        Assert.False(ProgressRow(host).IsVisible);

        vm.IsBusy = true;
        vm.CurrentStep = "Stopping the client";
        host.Pump();

        var row = ProgressRow(host);
        Assert.True(row.IsVisible);
        Assert.Contains(row.Children.OfType<TextBlock>(), t => t.Text == "Stopping the client");
    }

    // ---- pair: Maximum must track Total, not just Value tracking Completed. Binding Value alone
    // would leave Maximum at its default of 100, the exact stuck-bar symptom the step-count
    // contract exists to prevent, arriving here by a different route. ----
    [AvaloniaFact]
    public void The_progress_bar_maximum_tracks_total_and_value_tracks_completed()
    {
        var target = Target();
        using var host = ShowExpanded(out _, target);
        var vm = host.ViewModel.Unlocker;

        vm.Total = 5;
        vm.Completed = 3;
        host.Pump();

        var bar = ViewHost.Find<ProgressBar>(host.Window);
        Assert.Equal(5, bar.Maximum);
        Assert.Equal(3, bar.Value);

        // The denominator must reach the UI too: Total already reaches the bar's fill through
        // Maximum, but the text beside it must not show the numerator alone, or the user sees "3"
        // where the design shows "3/5".
        var texts = ProgressRow(host).Children.OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("3", texts);
        Assert.Contains("5", texts);
    }

    [AvaloniaFact]
    public void The_restart_button_is_hidden_until_a_result_reports_requires_elevation()
    {
        var target = Target();
        using var host = ShowExpanded(out var backend, target);

        var button = ViewHost.Find<Button>(host.Window, b => b.Content as string == "Restart as administrator");
        Assert.False(button.IsVisible);

        backend.NextResult = UnlockerResult.NeedsElevation();
        host.ViewModel.Unlocker.Targets[0].IsSelected = true;
        ViewHost.Find<Button>(host.Window, b => b.Content as string == "Install selected").Command!.Execute(null);
        Assert.True(ViewHost.PumpUntil(() => !host.ViewModel.Unlocker.IsBusy));

        Assert.True(button.IsVisible);
    }

    /// <summary>
    /// The only test that would catch an ItemsControl bound to the wrong collection, or a template
    /// that renders only its first item.
    /// </summary>
    [AvaloniaFact]
    public void Two_targets_render_two_rows()
    {
        var first = Target("/clients/ea", "EA app", ClientKind.EaApp);
        var second = Target("/clients/origin", "Origin", ClientKind.Origin);
        using var host = ShowExpanded(out _, first, second);

        var boxes = host.Window.GetVisualDescendants().OfType<CheckBox>()
            .Where(c => c.DataContext is UnlockerTargetViewModel).ToList();

        Assert.Equal(2, boxes.Count);
    }

    /// <summary>
    /// The row template's four TextBlocks, read as rendered: name, path, warning and status.
    /// Nothing else in the suite looks at them, so a deleted or re-pointed status TextBlock would
    /// ship in silence, and deleting the warning line leaves every per-target warning invisible
    /// while the view-model tests, which only read the property, stay green.
    /// </summary>
    [AvaloniaFact]
    public void Each_row_renders_its_name_its_path_and_its_status()
    {
        var first = Target("/clients/ea", "EA app", ClientKind.EaApp);
        var second = Target("/clients/origin", "Origin", ClientKind.Origin);
        using var host = ShowExpanded(out _, first, second);

        foreach (var target in (UnlockerTarget[])[first, second])
        {
            var texts = host.Window.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.DataContext is UnlockerTargetViewModel row
                            && row.ClientPath == target.ClientPath)
                .Select(t => t.Text)
                .ToList();

            Assert.Contains(target.DisplayName, texts);
            Assert.Contains(target.ClientPath, texts);
            Assert.Contains("Not installed", texts);
        }

        // Two rows, each with its own warning, both visible at once: warnings are per row because a
        // banner keys by id and a batch would collapse them to whichever target reported last.
        const string ea = "the staged copy failed";
        const string origin = "machine.ini was not found";
        host.ViewModel.Unlocker.Targets[0].Warning = ea;
        host.ViewModel.Unlocker.Targets[1].Warning = origin;
        host.Pump();

        // IsVisible as well as the text: a TextBlock still carries its Text when collapsed, so the
        // text alone would pass with the ObjectConverters.IsNotNull binding deleted or inverted.
        var warnings = ((string[])[ea, origin]).Select(text =>
            ViewHost.Find<TextBlock>(host.Window,
                t => t.DataContext is UnlockerTargetViewModel && t.Text == text)).ToList();
        Assert.All(warnings, w => Assert.True(w.IsVisible));

        // And back: with no warning the line must collapse rather than leave a blank row.
        host.ViewModel.Unlocker.Targets[0].Warning = null;
        host.ViewModel.Unlocker.Targets[1].Warning = null;
        host.Pump();
        Assert.All(warnings, w => Assert.False(w.IsVisible));

        // Nothing else in the suite executes or even looks for the two selection buttons, so they
        // could be dropped from the XAML in silence.
        Assert.NotNull(ViewHost.Find<Button>(host.Window,
            b => b.Content as string == "Select all"));
        Assert.NotNull(ViewHost.Find<Button>(host.Window, b => b.Content as string == "Select none"));
    }
}
