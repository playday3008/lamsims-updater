using LamSims.Core.Unlocking;

namespace LamSims.Core.Tests;

public class UnlockerConfigWriterTests
{
    private static UnlockerPaths Paths(TempDir dir) =>
        new(Path.Combine(dir.Path, "roaming"), Path.Combine(dir.Path, "common"));

    [Fact]
    public void The_embedded_dlc_list_declares_and_contains_one_hundred_and_fifty_five_packs()
    {
        var text = UnlockerConfigWriter.ReadDlcList();

        Assert.Contains("CNT=155", text);
        Assert.Equal(155, text.Split('\n').Count(l => l.StartsWith("NAM", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_embedded_default_config_carries_the_user_tunable_keys()
    {
        var text = UnlockerConfigWriter.ReadDefaultConfig();

        Assert.Contains("replaceDLCs=", text);
        Assert.Contains("languages=", text);
        Assert.Contains("showMessages=", text);
        Assert.Contains("debugMode=", text);
        Assert.Contains("fakeFullGame=", text);
    }

    [Fact]
    public async Task A_first_write_creates_both_files()
    {
        using var dir = new TempDir();
        var paths = Paths(dir);

        await UnlockerConfigWriter.WriteAsync(paths, CancellationToken.None);

        Assert.True(File.Exists(paths.ConfigFile));
        Assert.True(File.Exists(paths.DlcListFile));
    }

    /// <summary>
    /// The whole point: a reinstall must refresh the DLC list (new packs
    /// appear in it) while preserving config.ini (the user tunes it). Asserting only that config.ini
    /// survived would pass against a writer that touched neither file.
    /// </summary>
    [Fact]
    public async Task A_second_write_refreshes_the_dlc_list_and_preserves_an_edited_config()
    {
        using var dir = new TempDir();
        var paths = Paths(dir);
        await UnlockerConfigWriter.WriteAsync(paths, CancellationToken.None);

        await File.WriteAllTextAsync(paths.ConfigFile, "[config]\nreplaceDLCs=1\n");
        await File.WriteAllTextAsync(paths.DlcListFile, "clobbered");

        await UnlockerConfigWriter.WriteAsync(paths, CancellationToken.None);

        Assert.Equal("[config]\nreplaceDLCs=1\n", await File.ReadAllTextAsync(paths.ConfigFile));
        Assert.Contains("CNT=155", await File.ReadAllTextAsync(paths.DlcListFile));
    }

    /// <summary>
    /// The two resources were extracted from upstream's raw string literals in
    /// Sims4DLCUnlocker.cs, and the port writes them to disk verbatim, so every byte is part of the
    /// contract, including the config file's lack of a trailing newline and the DLC list's trailing
    /// blank line. The digests below were taken from upstream's literals directly; a hash rather than
    /// a length because a length passes for any edit that keeps the size.
    /// </summary>
    [Theory]
    [InlineData("LamSims.Core.Unlocking.Resources.config.ini", 1180,
                "4fe4d5f182c9b1eb680d1339030fd3a67e48c7db3859c2ad74ff703d0585d872")]
    [InlineData("LamSims.Core.Unlocking.Resources.dlc-list.ini", 25405,
                "df1bba7cc80b47602b2859cd5f92b5408b2e37b32797503bedc3dd2b33539980")]
    public void The_embedded_resources_are_upstream_byte_for_byte(string resource, int length,
                                                                 string digest)
    {
        using var stream = typeof(UnlockerConfigWriter).Assembly.GetManifestResourceStream(resource);

        // Not merely non-null: a LogicalName typo in the csproj is a runtime-only failure, and the
        // DLC list's own filename contains spaces.
        Assert.NotNull(stream);

        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);

        Assert.Equal(length, bytes.Length);
        Assert.Equal(digest, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
    }
}
