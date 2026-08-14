namespace LamSims.Core;

/// <summary>
/// Writes text to a file so a crash mid-write can never leave a torn or truncated file
/// where a good one belonged. The write lands at <c>path + ".tmp"</c> first and is flushed
/// to disk before the rename: without that flush, the temp file's bytes can still be
/// sitting in the page cache when the rename completes, and a power loss between the two
/// can leave a zero-length file at <paramref name="path"/> — the exact failure this
/// pattern exists to prevent.
/// </summary>
public static class AtomicFile
{
    public static async Task WriteAllTextAsync(string path, string contents, CancellationToken ct)
    {
        var tempPath = path + ".tmp";

        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync(contents.AsMemory(), ct);
                await writer.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
