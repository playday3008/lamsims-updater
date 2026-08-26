using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LamSims.Core.Logging;

namespace LamSims.Core.Unlocking.Wine;

/// <summary>
/// <see cref="IUnlockerHost"/> for one Wine prefix, so the whole install engine — every step, in
/// its shipped order — runs unchanged inside a prefix.
///
/// Almost every member's natural implementation is wrong here, and the comments say which and why.
/// The one that matters most is <see cref="RunningClientProcesses"/>: it is ALWAYS empty.
/// </summary>
public sealed class WineUnlockerHost(WinePrefix prefix, ILogSink? log = null)
    : IUnlockerHost
{
    private static readonly Dictionary<ClientRegistryKey, string> Keys = new()
    {
        [ClientRegistryKey.EaDesktop] = @"Software\Electronic Arts\EA Desktop",
        [ClientRegistryKey.EaDesktopWow6432] = @"Software\Wow6432Node\Electronic Arts\EA Desktop",
        [ClientRegistryKey.OriginWow6432] = @"Software\Wow6432Node\Origin",
        [ClientRegistryKey.Origin] = @"Software\Origin",
    };

    public bool IsAvailable =>
        File.Exists(Path.Combine(prefix.Root, "system.reg")) && Directory.Exists(prefix.DriveC);

    /// <summary>
    /// True. A prefix's contents are owned by the user running this application, so there is no
    /// privilege to acquire. Reporting false would make the engine's first step return
    /// NeedsElevation and the UI offer a relaunch that changes nothing.
    ///
    /// Not universally safe: a read-only mount or a prefix owned by another uid fails at the write
    /// instead, which the wrapper reports as its read-only message.
    /// </summary>
    public bool IsElevated => true;

    /// <summary>
    /// Whether any candidate key held a value at all, so the caller can tell "no client is
    /// registered here" from "one is registered and its path is gone". Those need different
    /// responses from the user and the engine only ever returns a list.
    /// </summary>
    public bool SawClientValue { get; private set; }

    /// <summary>
    /// The RESOLVED Linux path, never the raw registry value. On Linux
    /// <c>Path.GetDirectoryName(@"C:\Program Files\EA\EADesktop.exe")</c> returns an EMPTY string,
    /// because <c>\</c> is not a separator, so a host returning the raw value makes the engine's
    /// detection find nothing — on every prefix, with no error.
    /// </summary>
    public string? ReadClientPath(ClientRegistryKey key)
    {
        if (!Keys.TryGetValue(key, out var registryKey)) return null;

        // HKLM is system.reg and HKCU is user.reg, and a client can be registered in either: the
        // Windows host reads HKLM, but a Wine install done under a user profile lands in user.reg.
        // Reading only one file misses every prefix that used the other. The first file with a
        // stale path should not prevent checking the next file.
        string? firstUnresolvable = null;

        foreach (var file in new[] { "system.reg", "user.reg" })
        {
            var value = WineRegistryFile.ReadValue(Path.Combine(prefix.Root, file), registryKey,
                                                   "ClientPath");
            if (value?.Text is not { Length: > 0 } text) continue;

            SawClientValue = true;

            // For a REG_EXPAND_SZ, try expanding with each user's own directory, not just the
            // primary one. A client registered under a non-primary user must expand correctly.
            var candidates = value.Expandable
                ? prefix.WindowsUserDirectories.Select(dir =>
                    WineRegistryFile.Expand(text, Path.GetFileName(dir))).ToList()
                : [text];

            foreach (var windows in candidates)
            {
                var resolved = prefix.ResolveWindowsPath(windows);
                if (resolved is not null && File.Exists(resolved)) return resolved;

                if (firstUnresolvable is null) firstUnresolvable = windows;
            }
        }

        // If nothing resolved, report once with the first unresolvable path. firstUnresolvable is
        // only ever assigned after SawClientValue is set true in this same call, so a non-null
        // value here already implies SawClientValue — an explicit check of it would be redundant.
        if (firstUnresolvable is not null)
        {
            log?.Write(LogLine.Warning(
                $"{prefix.Environment.Describe()}: the client is registered at "
                + $"'{firstUnresolvable}' but that path does not exist in this prefix."));
        }

        return null;
    }

    /// <summary>
    /// ALWAYS empty, even when a client really is running. Wiring this to IWineProcesses breaks
    /// every install with the client running: the engine's gate goes non-empty, calls
    /// KillClientProcesses, is handed back survivors and returns a hard failure. Under Wine there
    /// is nothing to kill and nothing that needs killing — unlink+rename succeeds with a file
    /// mapped into a live process — so the client scan belongs to the wrapper, which turns it into
    /// a "restart the client" warning instead.
    /// </summary>
    public IReadOnlyList<string> RunningClientProcesses(IReadOnlyList<string> processNames) => [];

    /// <summary>
    /// Always empty, and nothing is ever signalled. Killing wineserver can lose registry state, and
    /// a prefix shared with a running game takes the game down with it.
    /// </summary>
    public IReadOnlyList<string> KillClientProcesses(IReadOnlyList<string> processNames,
                                                     TimeSpan perProcessTimeout) => [];

    /// <summary>
    /// Null. The Run key is inert under Wine: nothing there starts at a Linux login, so there is no
    /// value to back up and none to restore.
    /// </summary>
    public AutostartValue? ReadAutostartValue(string name) => null;

    public void WriteAutostartValue(string name, AutostartValue value)
    {
        // No-op. Storing it would make removal restore a value into a registry nothing reads.
    }

    public void RemoveAutostartValue(string name)
    {
        // No-op, and specifically not a throw: the engine's autostart step is documented non-fatal
        // but runs AFTER version.dll is on disk, so a throw here fails an install that worked.
    }

    public void DeleteScheduledTask(string name)
    {
        // No-op. Windows scheduled tasks do not exist in a prefix.
    }

    /// <summary>False, and unreachable: <see cref="IsElevated"/> is always true.</summary>
    public bool TryRelaunchElevated() => false;
}
