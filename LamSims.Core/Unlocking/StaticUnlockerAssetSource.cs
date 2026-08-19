using LamSims.Core.Downloading;

namespace LamSims.Core.Unlocking;

/// <param name="Url">The upstream project's own release asset.</param>
/// <param name="Size">Checked against Content-Length before any bytes transfer.</param>
/// <param name="Sha256">Lowercase hex.</param>
/// <param name="FileName">The cache entry's name under the download directory.</param>
public sealed record AssetPin(string Url, long Size, string Sha256, string FileName);

/// <summary>
/// The two unlocker DLLs, pinned by digest.
///
/// The URLs point at a mutable release tag, so if upstream re-uploads either asset this build
/// stops installing the unlocker until the pins are updated. The DLL is loaded into EA Desktop's
/// process, so an unnoticed substitution is worth failing on. The error names the cause so an
/// operator can tell a rotation from corruption.
/// </summary>
public sealed class StaticUnlockerAssetSource(
    HttpClient http,
    DownloadPaths paths,
    IReadOnlyDictionary<ClientKind, AssetPin>? pins = null,
    RetryOptions? retry = null) : IUnlockerAssetSource
{
    private const string Base = "https://github.com/Lamonsky/lamsims-updater/releases/download/Beta/";

    /// <summary>
    /// The shipped pin table. Injected rather than hardcoded so the tests can point at the local
    /// test server instead of the public internet; production passes nothing and gets this.
    /// </summary>
    public static IReadOnlyDictionary<ClientKind, AssetPin> ShippedPins { get; } =
        new Dictionary<ClientKind, AssetPin>
        {
            // EA Desktop is 64-bit and Origin is 32-bit, so these are not interchangeable: the EA
            // app DLL is PE32+ x86-64 and the Origin one is PE32 i386. A swapped pair would fail
            // silently at load time rather than loudly at install time.
            [ClientKind.EaApp] = new(Base + "ea_app_version.dll", 245248,
                "70553a2b4d53eddf1eeb290e346a9b562c71f52d46874bfb708e9d962469a736",
                "ea_app_version.dll"),
            [ClientKind.Origin] = new(Base + "origin_version.dll", 192512,
                "cf784476719a93e3fb8457a2d4c4580b691b6d04592b9a4467acf563f30d2b83",
                "origin_version.dll"),
        };

    private readonly IReadOnlyDictionary<ClientKind, AssetPin> _pins = pins ?? ShippedPins;
    private readonly RetryOptions _retry = retry ?? RetryOptions.Default;

    public async Task<string> GetDllAsync(ClientKind client, CancellationToken ct)
    {
        var pin = _pins[client];
        var cached = paths.UnlockerAssetFile(pin.FileName);

        if (File.Exists(cached) && await MatchesAsync(cached, pin.Sha256, ct)) return cached;

        paths.EnsureCreated();
        var temp = cached + ".incoming";

        try
        {
            // The header deadline is not optional: HttpFactory sets Timeout.InfiniteTimeSpan
            // because the pack path bounds itself per operation, so without this the fetch has no
            // timeout at all and a server that stalls after accepting hangs the install.
            using var request = new HttpRequestMessage(HttpMethod.Get, pin.Url);

            // Four arguments: HttpDeadline.SendAsync already passes ResponseHeadersRead itself.
            using var response = await HttpDeadline.SendAsync(http, request, _retry.HeaderTimeout, ct);
            response.EnsureSuccessStatusCode();

            var advertised = response.Content.Headers.ContentLength;
            if (advertised is not null && advertised != pin.Size)
                throw new SizeMismatchException(pin.Size, advertised.Value);

            await using (var incoming = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write,
                                                  FileShare.None))
            {
                // Each READ is bounded, not the whole transfer: a server that sends its headers and
                // then stops sending bytes must fail rather than hang the install. CopyToAsync would
                // wait forever, because HttpFactory's client has Timeout.InfiniteTimeSpan.
                var buffer = new byte[81920];
                var received = 0L;
                while (true)
                {
                    using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readDeadline.CancelAfter(_retry.ReadTimeout);

                    int read;
                    try
                    {
                        read = await incoming.ReadAsync(buffer, readDeadline.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new IOException(
                            $"The DLL at '{pin.Url}' stopped sending data mid-transfer.");
                    }

                    if (read == 0) break;

                    // A response without Content-Length skips the size gate above, and the payload
                    // is unauthenticated until the digest check. Without this a server that never
                    // stops sending fills the volume the pack queue is downloading into.
                    received += read;
                    if (received > pin.Size)
                        throw new IOException(
                            $"The DLL at '{pin.Url}' sent more than the expected {pin.Size} bytes.");

                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            var actual = await Sha256Verifier.ComputeAsync(temp, ct);
            if (!string.Equals(actual, pin.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UnlockerAssetMismatchException(pin.Url, pin.Sha256, actual);

            // A rename, not a copy: the payload is written to disk exactly once and no orphan is
            // left behind.
            AtomicFile.MoveIntoPlace(temp, cached);
            return cached;
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }
    }

    private static async Task<bool> MatchesAsync(string path, string expected, CancellationToken ct) =>
        string.Equals(await Sha256Verifier.ComputeAsync(path, ct), expected,
                      StringComparison.OrdinalIgnoreCase);
}
