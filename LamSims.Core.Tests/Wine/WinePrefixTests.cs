using System;
using System.IO;
using System.Linq;
using Xunit;
using LamSims.Core.Unlocking;
using LamSims.Core.Unlocking.Wine;

namespace LamSims.Core.Tests;

public class WinePrefixOpenTests
{
    private static TargetEnvironment Env(string detail = "test") =>
        new(EnvironmentSource.Wine, detail);

    [Fact]
    public void A_directory_with_system_reg_and_drive_c_opens()
    {
        using var f = new PrefixFixture();

        var prefix = WinePrefix.TryOpen(f.Root, Env(), "playday");

        Assert.NotNull(prefix);
        Assert.Equal(PathIdentity.Canonical(f.Root), PathIdentity.Canonical(prefix.Root));
        Assert.Equal(PathIdentity.Canonical(f.DriveC), PathIdentity.Canonical(prefix.DriveC));
    }

    /// <summary>
    /// The exact string, naming WHICH file is missing. A generic "not a prefix" would
    /// leave a user with a wrong Lutris path unable to tell it apart from a permissions problem.
    /// </summary>
    [Fact]
    public void A_directory_that_is_not_a_prefix_is_rejected_with_the_missing_file_named()
    {
        using var dir = new TempDir();
        var notes = new Notes();

        Assert.Null(WinePrefix.TryOpen(dir.Path, Env(), "playday", notes));

        Assert.Contains($"'{dir.Path}' is not a Wine prefix: no system.reg.", notes.Lines);
    }

    [Fact]
    public void A_prefix_with_system_reg_but_no_drive_c_names_drive_c()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "system.reg"), "#arch=win64\n");
        var notes = new Notes();

        Assert.Null(WinePrefix.TryOpen(dir.Path, Env(), "playday", notes));

        Assert.Contains($"'{dir.Path}' is not a Wine prefix: no drive_c.", notes.Lines);
    }

    /// <summary>
    /// Valve Proton keeps the prefix in a real `pfx/` subdirectory while the launcher config
    /// records the container. Without the one-shot retry every Valve-Proton-shaped Lutris, Heroic
    /// or Bottles prefix is rejected.
    /// </summary>
    [Fact]
    public void A_container_holding_pfx_opens_as_that_pfx()
    {
        using var dir = new TempDir();
        var container = Path.Combine(dir.Path, "compatdata", "1262600");
        var inner = Path.Combine(container, "pfx");
        Directory.CreateDirectory(Path.Combine(inner, "drive_c"));
        File.WriteAllText(Path.Combine(inner, "system.reg"), "#arch=win64\n");

        var prefix = WinePrefix.TryOpen(container, Env(), "steamuser");

        Assert.NotNull(prefix);
        Assert.Equal(PathIdentity.Canonical(inner), PathIdentity.Canonical(prefix.Root));
    }

    /// <summary>
    /// The retry is ONE level. A container two levels above a prefix is a rejection, not a search:
    /// Heroic's defaultSettings.winePrefix is a container the scanner enumerates, and
    /// a recursive retry here would make that enumeration redundant and find prefixes nobody
    /// configured.
    /// </summary>
    [Fact]
    public void The_pfx_retry_does_not_recurse()
    {
        using var dir = new TempDir();
        var deep = Path.Combine(dir.Path, "a", "pfx", "pfx");
        Directory.CreateDirectory(Path.Combine(deep, "drive_c"));
        File.WriteAllText(Path.Combine(deep, "system.reg"), "#arch=win64\n");

        Assert.Null(WinePrefix.TryOpen(Path.Combine(dir.Path, "a"), Env(), "playday"));
    }

    [Theory]
    [InlineData("win64", WineArch.Win64)]
    [InlineData("win32", WineArch.Win32)]
    public void The_arch_comes_from_the_reg_header(string written, WineArch expected)
    {
        using var f = new PrefixFixture(arch: written);

        Assert.Equal(expected, f.Open().Arch);
    }

    /// <summary>
    /// Treated as win64 with a diagnostic, never as a rejection. A prefix whose header
    /// we cannot read still holds a client we can unlock.
    /// </summary>
    [Fact]
    public void A_missing_arch_line_is_win64_with_a_note()
    {
        using var f = new PrefixFixture();
        f.WriteSystemReg("WINE REGISTRY Version 2\n");
        f.WriteUserReg("WINE REGISTRY Version 2\n");
        var notes = new Notes();

        Assert.Equal(WineArch.Win64, f.Open(notes).Arch);
        Assert.True(notes.Any("architecture"), string.Join("\n", notes.Lines));
    }

    /// <summary>
    /// GE-Proton and umu write the marker at the prefix root; Valve Proton writes it one level up,
    /// beside `pfx`. Checking only the root misses every Valve Proton prefix, and the reader then
    /// picks the wrong Windows user for exactly the population that always uses `steamuser`.
    /// </summary>
    [Fact]
    public void A_proton_marker_beside_pfx_is_seen()
    {
        using var dir = new TempDir();
        var container = Path.Combine(dir.Path, "compatdata", "1262600");
        var inner = Path.Combine(container, "pfx");
        Directory.CreateDirectory(Path.Combine(inner, "drive_c", "users", "steamuser"));
        File.WriteAllText(Path.Combine(inner, "system.reg"), "#arch=win64\n");
        File.WriteAllText(Path.Combine(container, "config_info"), "GE-Proton10-34\n");

        var prefix = WinePrefix.TryOpen(container, Env(), "playday");

        Assert.NotNull(prefix);
        Assert.True(prefix.ProtonManaged);
        Assert.Equal("steamuser", Path.GetFileName(prefix.PrimaryUserDirectory));
    }

    [Fact]
    public void A_plain_prefix_uses_the_injected_user_name()
    {
        using var f = new PrefixFixture(user: "playday");
        f.AddUser("steamuser");

        var prefix = f.Open();

        Assert.False(prefix.ProtonManaged);
        Assert.Equal("playday", Path.GetFileName(prefix.PrimaryUserDirectory));
        Assert.Single(prefix.WindowsUserDirectories);
    }

    /// <summary>
    /// The pair. Asserting only the count would pass against a list that included `Public`, whose
    /// AppData the unlocker DLL never reads, and asserting only the names would pass against an
    /// implementation that returned every user for every prefix.
    /// </summary>
    [Fact]
    public void An_unrecognisable_user_writes_to_every_non_public_user_with_a_note()
    {
        using var f = new PrefixFixture(user: "nobody-here");
        Directory.Delete(Path.Combine(f.DriveC, "users", "nobody-here"), recursive: true);
        f.AddUser("steamuser");
        f.AddUser("crossover");
        var notes = new Notes();

        var prefix = f.Open(notes);

        Assert.Equal(["crossover", "steamuser"],
                     prefix.WindowsUserDirectories.Select(Path.GetFileName).Order());
        Assert.True(notes.Any("has more than one Windows user; the configuration will be written under each."),
                    string.Join("\n", notes.Lines));
    }
}
