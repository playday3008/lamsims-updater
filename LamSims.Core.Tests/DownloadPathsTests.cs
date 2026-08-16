using LamSims.Core.Downloading;

namespace LamSims.Core.Tests;

public class DownloadPathsTests
{
    [Fact]
    public void Root_defaults_under_local_application_data()
    {
        var paths = new DownloadPaths();

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "lamsims-updater", "downloads");

        Assert.Equal(expected, paths.Root);
    }

    [Fact]
    public void Root_honours_the_override()
    {
        using var temp = new TempDir();

        var paths = new DownloadPaths(temp.Path);

        Assert.Equal(temp.Path, paths.Root);
    }

    [Fact]
    public void File_names_follow_the_code()
    {
        using var temp = new TempDir();
        var paths = new DownloadPaths(temp.Path);

        Assert.Equal(Path.Combine(temp.Path, "EP01.part"), paths.PartFile("EP01"));
        Assert.Equal(Path.Combine(temp.Path, "EP01.part.json"), paths.StateFile("EP01"));
        Assert.Equal(Path.Combine(temp.Path, "EP01.zip"), paths.ArchiveFile("EP01"));
        Assert.Equal(Path.Combine(temp.Path, "EP01.zip.bad"), paths.QuarantineFile("EP01"));
        Assert.Equal(Path.Combine(temp.Path, "EP01.zip.json"), paths.ArchiveDigestFile("EP01"));
    }

    [Fact]
    public void Lock_file_sits_beside_the_archive_and_rejects_a_path_character()
    {
        var paths = new DownloadPaths("/tmp/lamsims-x");

        Assert.Equal(Path.Combine("/tmp/lamsims-x", "EP01.lock"), paths.LockFile("EP01"));
        Assert.Throws<ArgumentException>(() => paths.LockFile("../EP01"));
    }

    [Fact]
    public void EnsureCreated_creates_the_root()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "nested", "downloads");
        var paths = new DownloadPaths(root);

        paths.EnsureCreated();

        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public void Blank_code_is_rejected()
    {
        var paths = new DownloadPaths();

        Assert.Throws<ArgumentException>(() => paths.PartFile("  "));
    }
}
