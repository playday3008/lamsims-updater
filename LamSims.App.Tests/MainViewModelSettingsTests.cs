using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LamSims.App.ViewModels;

namespace LamSims.App.Tests;

public class MainViewModelSettingsTests
{
    [Fact(Timeout = 15000)]
    public async Task Browsing_for_a_game_folder_sets_it_saves_it_and_rescans()
    {
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        vm.BuildRows([Packs.Entry()]);
        host.Pickers.NextFolder = host.GameDirectory;

        await vm.BrowseGameCommand.ExecuteAsync(null);

        Assert.Equal(host.GameDirectory, vm.GameDirectory);
        Assert.True(vm.ScanCount > 0);
        Assert.Single(host.Saved);
    }

    [Fact(Timeout = 15000)]
    public async Task A_cancelled_picker_changes_nothing()
    {
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;
        vm.GameDirectory = host.GameDirectory;
        host.Pickers.NextFolder = null;

        await vm.BrowseGameCommand.ExecuteAsync(null);

        Assert.Equal(host.GameDirectory, vm.GameDirectory);
        Assert.Empty(host.Saved);
    }

    [Fact(Timeout = 15000)]
    public async Task A_change_is_not_written_until_the_debounce_has_elapsed()
    {
        // A slider drag must not write settings.json once per pixel. The clock is injected so
        // this test moves time instead of sleeping.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;

        vm.Connections = 2;
        vm.Connections = 4;
        vm.Connections = 8;

        await vm.FlushDueSettingsAsync(CancellationToken.None);
        Assert.Empty(host.Saved);

        host.Clock.Advance(TimeSpan.FromSeconds(1));
        await vm.FlushDueSettingsAsync(CancellationToken.None);

        Assert.Single(host.Saved);
        Assert.Equal(8, host.Saved[0].Connections);
        Assert.Equal(1, vm.SettingsWriteCount);
    }

    [Fact(Timeout = 15000)]
    public async Task Connections_and_the_download_folder_say_they_need_a_restart()
    {
        // Connections is baked into the handler's MaxConnectionsPerServer and into the
        // downloader's constructor-held options; DownloadPaths is a PackQueue constructor field
        // and is what PackLock derives from. Applying either live means rebuilding the queue,
        // the runner, the client and the bridge for a setting changed once.
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;

        Assert.False(vm.RestartNoticeVisible);

        vm.Connections = 12;
        Assert.True(vm.RestartNoticeVisible);

        await vm.FlushSettingsNowAsync(CancellationToken.None);
        Assert.Equal(12, host.Saved[0].Connections);
    }

    [Fact]
    public void Settings_loaded_at_startup_are_shown_rather_than_overwritten_by_defaults()
    {
        // Seeding through the property setters would raise the restart notice and queue a save
        // for a value the user already chose.
        var vm = TestHost.ViewModel(out var host, seed: settings =>
        {
            settings.Connections = 16;
            settings.DownloadDirectory = "/tmp/downloads";
        });
        using var _h = host;

        Assert.Equal(16, vm.Connections);
        Assert.Equal("/tmp/downloads", vm.DownloadDirectory);
        Assert.False(vm.RestartNoticeVisible);
    }

    [Fact(Timeout = 15000)]
    public async Task A_failed_save_becomes_a_banner_rather_than_an_exception()
    {
        var vm = TestHost.ViewModel(out var host,
            save: (_, _) => throw new IOException("the settings file could not be written"));
        using var _h = host;

        vm.Connections = 3;
        await vm.FlushSettingsNowAsync(CancellationToken.None);

        Assert.Contains(vm.Banners, b => b.Id == "settings" && b.Kind == BannerKind.Warning);
    }
}
