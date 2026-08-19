using LamSims.Core.Unlocking;

namespace LamSims.Core.Tests;

/// <summary>
/// The only thing faked in the unlocker tests. The filesystem stays real, under TempDir, because
/// Core touches File and Directory directly.
/// </summary>
public sealed class FakeUnlockerHost : IUnlockerHost
{
    public bool IsAvailable { get; set; } = true;
    public bool IsElevated { get; set; } = true;

    public Dictionary<ClientRegistryKey, string?> ClientPaths { get; } = [];
    public Dictionary<string, string> AutostartValues { get; } = [];

    /// <summary>Process names KillClientProcesses reports as still running when it gave up.</summary>
    public List<string> Survivors { get; } = [];
    public List<string> Running { get; } = [];

    public List<string> Calls { get; } = [];

    public string? ReadClientPath(ClientRegistryKey key)
    {
        Calls.Add($"ReadClientPath:{key}");
        return ClientPaths.TryGetValue(key, out var path) ? path : null;
    }

    public string? ReadAutostartValue(string name)
    {
        Calls.Add($"ReadAutostart:{name}");
        return AutostartValues.TryGetValue(name, out var v) ? v : null;
    }

    /// <summary>
    /// Set to make both autostart writes throw the way a group-policy-restricted Run key does. The
    /// real host guards its read paths against SecurityException but not its writes, so the
    /// backend's catch filters have to cover them.
    /// </summary>
    public bool ThrowSecurityOnAutostartWrite { get; set; }

    public void WriteAutostartValue(string name, string value)
    {
        Calls.Add($"WriteAutostart:{name}={value}");
        if (ThrowSecurityOnAutostartWrite)
            throw new System.Security.SecurityException("Requested registry access is not allowed.");
        AutostartValues[name] = value;
    }

    public void RemoveAutostartValue(string name)
    {
        Calls.Add($"RemoveAutostart:{name}");
        if (ThrowSecurityOnAutostartWrite)
            throw new System.Security.SecurityException("Requested registry access is not allowed.");
        AutostartValues.Remove(name);
    }

    /// <summary>The names the backend asked about, so a test can assert the exact-name set.</summary>
    public List<IReadOnlyList<string>> Asked { get; } = [];

    public IReadOnlyList<string> RunningClientProcesses(IReadOnlyList<string> processNames)
    {
        Asked.Add(processNames);
        Calls.Add($"Running:{string.Join(",", processNames)}");
        return Running;
    }

    public IReadOnlyList<string> KillClientProcesses(IReadOnlyList<string> processNames,
                                                    TimeSpan perProcessTimeout)
    {
        Asked.Add(processNames);
        Calls.Add($"Kill:{string.Join(",", processNames)}");
        return Survivors;
    }

    public void DeleteScheduledTask(string name) => Calls.Add($"DeleteTask:{name}");

    public bool RelaunchAccepted { get; set; } = true;

    public bool TryRelaunchElevated()
    {
        Calls.Add("Relaunch");
        return RelaunchAccepted;
    }
}
