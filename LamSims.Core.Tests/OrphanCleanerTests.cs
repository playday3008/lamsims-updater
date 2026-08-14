using LamSims.Core.Downloading;

namespace LamSims.Core.Tests;

public class OrphanCleanerTests
{
    [Fact]
    public void Deletes_partials_whose_code_is_unknown()
    {
        using var temp = new TempDir();
        temp.Write("EP99.part", "x");
        temp.Write("EP99.part.json", "{}");
        var cleaner = new OrphanCleaner(new DownloadPaths(temp.Path));

        var deleted = cleaner.CleanOrphans(new HashSet<string> { "EP01" });

        Assert.Equal(2, deleted.Count);
        Assert.False(File.Exists(temp.File("EP99.part")));
        Assert.False(File.Exists(temp.File("EP99.part.json")));
    }

    [Fact]
    public void Keeps_partials_whose_code_is_known()
    {
        using var temp = new TempDir();
        temp.Write("EP01.part", "x");
        temp.Write("EP01.part.json", "{}");
        var cleaner = new OrphanCleaner(new DownloadPaths(temp.Path));

        var deleted = cleaner.CleanOrphans(new HashSet<string> { "EP01" });

        Assert.Empty(deleted);
        Assert.True(File.Exists(temp.File("EP01.part")));
    }

    [Fact]
    public void Never_deletes_quarantined_archives_or_completed_archives()
    {
        using var temp = new TempDir();
        temp.Write("EP99.zip.bad", "x");
        temp.Write("EP99.zip", "x");
        var cleaner = new OrphanCleaner(new DownloadPaths(temp.Path));

        var deleted = cleaner.CleanOrphans(new HashSet<string>());

        Assert.Empty(deleted);
        Assert.True(File.Exists(temp.File("EP99.zip.bad")));
        Assert.True(File.Exists(temp.File("EP99.zip")));
    }

    [Fact]
    public void Missing_root_is_not_an_error()
    {
        using var temp = new TempDir();
        var cleaner = new OrphanCleaner(new DownloadPaths(Path.Combine(temp.Path, "absent")));

        Assert.Empty(cleaner.CleanOrphans(new HashSet<string>()));
    }
}
