using System;
using System.IO;
using Xunit;
using LamSims.Core.Settings;
using LamSims.Core.Unlocking;

namespace LamSims.Core.Tests;

public class UnlockerPathsTests
{
    [Fact]
    public void Overridden_roots_place_every_derived_path_under_them()
    {
        var paths = new UnlockerPaths("/roaming", "/common");

        Assert.Equal(Path.Combine("/roaming", "anadius", "EA DLC Unlocker v2"), paths.ConfigDirectory);
        Assert.Equal(Path.Combine(paths.ConfigDirectory, "config.ini"), paths.ConfigFile);
        Assert.Equal(Path.Combine(paths.ConfigDirectory, "g_The Sims 4.ini"), paths.DlcListFile);
        Assert.Equal(Path.Combine("/common", "EA Desktop", "machine.ini"), paths.MachineIniFile);
    }

    [Fact]
    public void Unset_roots_fall_back_to_the_platform_special_folders()
    {
        var paths = new UnlockerPaths();

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), paths.Roaming);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     paths.CommonAppData);
    }

    /// <summary>
    /// The pair that matters: the backup must be under the app root, NOT under the unlocker config
    /// directory that removal deletes. Asserting only "it is a path" would pass either way.
    /// </summary>
    [Fact]
    public void The_autostart_backup_lives_outside_the_directory_removal_deletes()
    {
        var app = new AppPaths("/app-root");
        var unlocker = new UnlockerPaths("/roaming", "/common");

        Assert.StartsWith("/app-root", app.UnlockerAutostartBackupFile);
        Assert.DoesNotContain("anadius", app.UnlockerAutostartBackupFile);
        Assert.False(app.UnlockerAutostartBackupFile.StartsWith(unlocker.ConfigDirectory,
                                                               StringComparison.Ordinal));
    }
}
