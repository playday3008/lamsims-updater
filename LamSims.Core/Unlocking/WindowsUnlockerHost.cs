using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;

namespace LamSims.Core.Unlocking;

/// <summary>
/// The only Windows-specific class here. Every member is straight-line: a registry read, a process
/// call, a delete.
///
/// CA1416 enforces the platform annotation during ordinary Linux builds, because the analyzer is
/// platform-independent. It does NOT cover <see cref="DeleteScheduledTask"/>: that shells out to
/// schtasks.exe, which carries no platform annotation, so that member is guarded by
/// <see cref="IsAvailable"/> instead.
/// </summary>
public sealed class WindowsUnlockerHost : IUnlockerHost
{
    public bool IsAvailable => OperatingSystem.IsWindows();

    private static readonly Dictionary<ClientRegistryKey, string> Keys = new()
    {
        [ClientRegistryKey.EaDesktop] = @"SOFTWARE\Electronic Arts\EA Desktop",
        [ClientRegistryKey.OriginWow6432] = @"SOFTWARE\WOW6432Node\Origin",
        [ClientRegistryKey.Origin] = @"SOFTWARE\Origin",
    };

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsElevated
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException)
            {
                return false;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadValue(RegistryKey root, string subKey, string name)
    {
        using var key = root.OpenSubKey(subKey);
        return key?.GetValue(name) as string;
    }

    public string? ReadClientPath(ClientRegistryKey key)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return ReadValue(Registry.LocalMachine, Keys[key], "ClientPath");
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException
                                      or IOException)
        {
            // A missing or unreadable key must not throw: detection walks all three in order.
            return null;
        }
    }

    public string? ReadAutostartValue(string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return ReadValue(Registry.CurrentUser, RunKey, name);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException
                                      or IOException)
        {
            return null;
        }
    }

    public void WriteAutostartValue(string name, string value)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.SetValue(name, value, RegistryValueKind.String);
    }

    public void RemoveAutostartValue(string name)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(name) is not null) key.DeleteValue(name);
    }

    public IReadOnlyList<string> RunningClientProcesses(IReadOnlyList<string> processNames) =>
        Matching(processNames).Select(p => p.ProcessName).Distinct(StringComparer.Ordinal).ToList();

    public IReadOnlyList<string> KillClientProcesses(IReadOnlyList<string> processNames,
                                                    TimeSpan perProcessTimeout)
    {
        var survivors = new List<string>();

        foreach (var process in Matching(processNames))
        {
            using (process)
            {
                try
                {
                    process.Kill(entireProcessTree: true);

                    // The wait result is kept: a client that outlives its timeout still holds the
                    // client directory open, and the caller has to know that now.
                    if (!process.WaitForExit((int)perProcessTimeout.TotalMilliseconds))
                        survivors.Add(process.ProcessName);
                }
                catch (Exception e) when (e is InvalidOperationException or SystemException)
                {
                    // Already gone between enumeration and kill, or not ours to kill.
                    if (!process.HasExited) survivors.Add(process.ProcessName);
                }
            }
        }

        return survivors.Distinct(StringComparer.Ordinal).ToList();
    }

    // Exact-name lookup only. The name set is the backend's (spec §4.2), so this file holds no
    // policy at all — which is what makes it safe to ship without tests.
    private static List<Process> Matching(IReadOnlyList<string> processNames) =>
        processNames
            .SelectMany(name =>
            {
                try { return Process.GetProcessesByName(name); }
                catch (InvalidOperationException) { return []; }
            })
            .ToList();

    public void DeleteScheduledTask(string name)
    {
        if (!OperatingSystem.IsWindows()) return;

        // schtasks rather than dahall's TaskScheduler package: a single delete is all that remains
        // once the port stops creating the task, and this keeps the phase at zero new dependencies.
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe")
            {
                ArgumentList = { "/Delete", "/TN", name, "/F" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            // A non-zero exit means the task was absent, which is the normal case and not an error.
            process?.WaitForExit(10_000);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Nothing to do: the caller treats this step as non-fatal.
        }
    }

    public bool TryRelaunchElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;

        var exe = Environment.ProcessPath;
        if (exe is null) return false;

        try
        {
            // "runas" raises the UAC prompt. A declined prompt throws Win32Exception with
            // ERROR_CANCELLED (1223), which is a false return and not a failure: the caller must
            // stay running, because it has not shut anything down yet.
            using var started = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            return started is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
