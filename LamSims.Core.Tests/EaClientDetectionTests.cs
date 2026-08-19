using LamSims.Core.Settings;
using LamSims.Core.Unlocking;

namespace LamSims.Core.Tests;

public class EaClientDetectionTests
{
    private static (EaClientUnlockerBackend Backend, FakeUnlockerHost Host) Build(TempDir dir)
    {
        var host = new FakeUnlockerHost();
        var backend = new EaClientUnlockerBackend(
            host,
            new UnlockerPaths(Path.Combine(dir.Path, "roaming"), Path.Combine(dir.Path, "common")),
            new AppPaths(Path.Combine(dir.Path, "app")),
            new FakeDelayProvider());
        return (backend, host);
    }

    /// <summary>Creates the client's directory and returns the exe path the registry would hold.</summary>
    private static string InstalledClient(TempDir dir, string folder, string exe)
    {
        var directory = Path.Combine(dir.Path, folder);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, exe);
    }

    [Theory]
    [InlineData(ClientRegistryKey.EaDesktop, ClientKind.EaApp, "EA app")]
    [InlineData(ClientRegistryKey.OriginWow6432, ClientKind.Origin, "Origin")]
    [InlineData(ClientRegistryKey.Origin, ClientKind.Origin, "Origin")]
    public async Task Each_key_alone_yields_its_client(ClientRegistryKey key, ClientKind kind,
                                                       string display)
    {
        using var dir = new TempDir();
        var (backend, host) = Build(dir);
        host.ClientPaths[key] = InstalledClient(dir, "client", "EADesktop.exe");

        var targets = await backend.DetectTargetsAsync(CancellationToken.None);

        var target = Assert.Single(targets);
        Assert.Equal(kind, target.Client);
        Assert.Equal(display, target.DisplayName);
        Assert.Equal("windows-native", target.BackendId);
        Assert.Equal(Path.Combine(dir.Path, "client"), target.ClientPath);
    }

    // Asserting the count as well as the kind: returning both clients would still satisfy a
    // "contains EaApp" check.
    [Fact]
    public async Task Ea_desktop_wins_when_more_than_one_key_is_present()
    {
        using var dir = new TempDir();
        var (backend, host) = Build(dir);
        host.ClientPaths[ClientRegistryKey.EaDesktop] = InstalledClient(dir, "ea", "EADesktop.exe");
        host.ClientPaths[ClientRegistryKey.Origin] = InstalledClient(dir, "origin", "Origin.exe");

        var targets = await backend.DetectTargetsAsync(CancellationToken.None);

        Assert.Single(targets);
        Assert.Equal(ClientKind.EaApp, targets[0].Client);
    }

    [Fact]
    public async Task A_key_whose_directory_does_not_exist_is_skipped_not_returned()
    {
        using var dir = new TempDir();
        var (backend, host) = Build(dir);
        host.ClientPaths[ClientRegistryKey.EaDesktop] =
            Path.Combine(dir.Path, "not-installed", "EADesktop.exe");
        host.ClientPaths[ClientRegistryKey.Origin] = InstalledClient(dir, "origin", "Origin.exe");

        var targets = await backend.DetectTargetsAsync(CancellationToken.None);

        Assert.Equal(ClientKind.Origin, Assert.Single(targets).Client);
    }

    [Fact]
    public async Task No_keys_at_all_yields_an_empty_list_rather_than_throwing()
    {
        using var dir = new TempDir();
        var (backend, _) = Build(dir);

        Assert.Empty(await backend.DetectTargetsAsync(CancellationToken.None));
    }

    // Every key is scripted present, because an empty list is also what "no keys at all" produces;
    // on Linux a backend that ignored IsAvailable would call registry members that throw.
    [Fact]
    public async Task An_unavailable_host_is_not_consulted_at_all()
    {
        using var dir = new TempDir();
        var (backend, host) = Build(dir);
        host.IsAvailable = false;
        foreach (var key in Enum.GetValues<ClientRegistryKey>())
            host.ClientPaths[key] = InstalledClient(dir, "client", "EADesktop.exe");

        Assert.Empty(await backend.DetectTargetsAsync(CancellationToken.None));
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("ReadClientPath", StringComparison.Ordinal));
        Assert.False(backend.IsSupported);
    }

    [Fact]
    public async Task Status_is_installed_exactly_when_version_dll_is_present()
    {
        using var dir = new TempDir();
        var (backend, host) = Build(dir);
        var exe = InstalledClient(dir, "client", "EADesktop.exe");
        host.ClientPaths[ClientRegistryKey.EaDesktop] = exe;
        var target = (await backend.DetectTargetsAsync(CancellationToken.None))[0];

        Assert.Equal(UnlockerState.NotInstalled,
                     (await backend.GetStatusAsync(target, CancellationToken.None)).State);

        await File.WriteAllTextAsync(Path.Combine(target.ClientPath, "version.dll"), "x");

        Assert.Equal(UnlockerState.Installed,
                     (await backend.GetStatusAsync(target, CancellationToken.None)).State);
    }
}
