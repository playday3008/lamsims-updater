using System.Linq;
using System.Threading.Tasks;
using Xunit;
using LamSims.App.ViewModels;
using LamSims.Core.Queueing;

namespace LamSims.App.Tests;

public class MainViewModelLogTests
{
    /// <summary>A banner is the one thing a user is guaranteed to have seen; the log keeps it
    /// after the banner is dismissed.</summary>
    [Fact(Timeout = 15000)]
    public async Task A_raised_banner_is_mirrored_into_the_log_at_its_own_severity()
    {
        var vm = TestHost.ViewModel(out var host, settingsError: "settings were unreadable");
        using var _h = host;

        Assert.Contains(vm.Log.Lines, l => l.Text.Contains("settings were unreadable") && l.IsWarning);

        await vm.ShutdownAsync();
    }

    [Fact(Timeout = 15000)]
    public async Task A_downloading_snapshot_ticks_the_log()
    {
        var vm = TestHost.ViewModel(out var host);
        using var _h = host;

        vm.ApplyUpdate(new QueueUpdate(QueueState.Running, [
            new QueueItemSnapshot("EP01", "Get to Work", QueueItemState.Downloading,
                120, 1000, 8_100_000, null, null, null, [])
        ]));

        Assert.Contains(vm.Log.Lines, l => l.Code == "EP01" && l.Text.Contains("12%"));

        await vm.ShutdownAsync();
    }
}
