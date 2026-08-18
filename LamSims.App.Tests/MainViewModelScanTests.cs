using LamSims.App.ViewModels;

namespace LamSims.App.Tests;

public class MainViewModelScanTests
{
    private static QueueItemSnapshot Snap(string code, QueueItemState state) =>
        new(code, code, state, 0, 0, 0, null, null, null, Array.Empty<string>());

    private static void Deliver(MainViewModel vm, QueueUpdate update)
    {
        // Exactly what QueueBridge.Apply does: rows first, then the view model.
        foreach (var item in update.Items) vm.RowFor(item.Code)?.ApplyQueue(item);

        vm.ApplyUpdate(update);
    }

    [Fact]
    public void A_scan_applies_disk_state_to_every_row()
    {
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        vm.BuildRows([Packs.Entry(), Packs.Entry("EP02", "Get Together")]);
        vm.GameDirectory = host.GameDirectory;
        Directory.CreateDirectory(Path.Combine(host.GameDirectory, "EP01"));

        vm.ScanCommand.Execute(null);

        Assert.Equal(PackInstallState.InstalledUnverified, vm.Rows[0].InstallState);
        Assert.Equal(PackInstallState.NotInstalled, vm.Rows[1].InstallState);
    }

    [Fact]
    public void An_unreadable_game_directory_is_a_banner_and_disables_adding()
    {
        // Every pack reads NotInstalled when the directory cannot be read, so without the flag
        // an unreadable directory is indistinguishable from an empty one.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        vm.BuildRows([Packs.Entry()]);
        vm.GameDirectory = Path.Combine(host.Root, "no-such-directory");

        vm.ScanCommand.Execute(null);

        Assert.False(vm.GameDirectoryReadable);
        Assert.Contains(vm.Banners, b => b.Id == "game-directory");
        Assert.False(vm.AddSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void A_readable_directory_clears_the_banner_again()
    {
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        vm.BuildRows([Packs.Entry()]);
        vm.GameDirectory = Path.Combine(host.Root, "no-such-directory");
        vm.ScanCommand.Execute(null);

        vm.GameDirectory = host.GameDirectory;
        vm.ScanCommand.Execute(null);

        Assert.True(vm.GameDirectoryReadable);
        Assert.DoesNotContain(vm.Banners, b => b.Id == "game-directory");
    }

    [Fact]
    public void A_completed_pack_triggers_a_rescan_so_the_row_reads_disk_truth()
    {
        // The queue's word for a finished pack is "Completed"; the marker the install wrote is
        // what says Installed. The rescan is also what clears the terminal overlay.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        vm.BuildRows([Packs.Entry()]);
        vm.GameDirectory = host.GameDirectory;

        Deliver(vm, new QueueUpdate(QueueState.Running, [Snap("EP01", QueueItemState.Downloading)]));
        Assert.Equal(QueueItemState.Downloading, vm.Rows[0].QueueState);

        Directory.CreateDirectory(Path.Combine(host.GameDirectory, "EP01"));
        Deliver(vm, new QueueUpdate(QueueState.Idle, [Snap("EP01", QueueItemState.Completed)]));

        Assert.Null(vm.Rows[0].QueueState);
        Assert.Equal("Installed (not verified by this tool)", vm.Rows[0].StatusText);
    }

    [Fact]
    public void A_second_update_naming_the_same_completion_does_not_rescan_again()
    {
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        vm.BuildRows([Packs.Entry()]);
        vm.GameDirectory = host.GameDirectory;

        var completed = new QueueUpdate(QueueState.Idle, [Snap("EP01", QueueItemState.Completed)]);

        Deliver(vm, completed);
        var after = vm.ScanCount;
        Deliver(vm, completed);

        Assert.Equal(after, vm.ScanCount);
    }
}
