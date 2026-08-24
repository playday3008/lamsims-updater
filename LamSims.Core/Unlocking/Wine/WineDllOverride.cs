using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LamSims.Core.Settings;

namespace LamSims.Core.Unlocking.Wine;

/// <param name="PrefixPath">In full, so a hashed-key collision cannot make a record vouch for
/// another prefix.</param>
/// <param name="WroteRegistry">
/// Whether this application wrote the entry. Removal branches on THIS, never on what a launcher
/// config now says: a user who adds a Lutris override after installing must not be left with our
/// registry write in place for ever.
/// </param>
/// <param name="PriorValue">Written back verbatim on removal, or null to remove the line.</param>
/// <param name="CreatedBlock">Whether the key block itself was appended, and so may be removed.</param>
/// <param name="SkippedBecause">Why nothing was written, when nothing was.</param>
public sealed record WineOverrideRecord(string PrefixPath, bool WroteRegistry, string? PriorValue,
                                        bool CreatedBlock, string? SkippedBecause);

/// <summary>
/// One record per PREFIX, not per client: a single <c>"*version"</c> entry in a single user.reg
/// serves every client in that prefix, exactly as one configuration directory does.
/// </summary>
public sealed class WineOverrideStore(AppPaths paths)
{
    private string FileFor(string prefixPath) =>
        Path.Combine(paths.WineOverrideDirectory, $"{PathIdentity.DirectoryKey(prefixPath)}.json");

    public WineOverrideRecord? Read(string prefixPath)
    {
        var record = ReadFile(FileFor(prefixPath));

        // A hashed key is not proof. A record naming a different prefix is treated as absent rather
        // than trusted, so a collision cannot make one prefix's undo run against another's.
        return record is not null
               && PathIdentity.Canonical(record.PrefixPath) == PathIdentity.Canonical(prefixPath)
            ? record
            : null;
    }

    /// <summary>
    /// Reports rather than throws, matching <see cref="Delete"/> and the rest of this file's
    /// contract. A record that cannot be saved after the registry has been written is the one loss
    /// nothing else can recover from — the override is in place with nothing on disk that records
    /// what it replaced — so the caller has to be able to say so.
    /// </summary>
    /// <returns>Null on success; the reason otherwise.</returns>
    public async Task<string?> WriteAsync(WineOverrideRecord record, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(paths.WineOverrideDirectory);
            await AtomicFile.WriteAllTextAsync(FileFor(record.PrefixPath),
                                              JsonSerializer.Serialize(record), ct);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    public void Delete(string prefixPath)
    {
        try
        {
            var file = FileFor(prefixPath);
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A record that cannot be deleted leaves a stale claim, which the next sweep reports.
            // It must not fail a removal that has already put the prefix right.
        }
    }

    public IReadOnlyList<WineOverrideRecord> All()
    {
        var found = new List<WineOverrideRecord>();

        try
        {
            if (!Directory.Exists(paths.WineOverrideDirectory)) return found;

            foreach (var file in Directory.EnumerateFiles(paths.WineOverrideDirectory, "*.json"))
                if (ReadFile(file) is { } record) found.Add(record);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An unreadable store sweeps nothing, which is the safe direction: it leaves records in
            // place rather than deleting ones it could not read.
        }

        return found;
    }

    private static WineOverrideRecord? ReadFile(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;

            var record = JsonSerializer.Deserialize<WineOverrideRecord>(File.ReadAllText(file));

            return string.IsNullOrWhiteSpace(record?.PrefixPath) ? null : record;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The one registry value this application writes, and the record that lets it be undone.
///
/// It must be <c>"*version"</c> and not <c>"version"</c>: the <c>*</c> prefix marks the entry
/// path-independent, and the lookup tries the resolved module path first and then
/// <c>*&lt;basename&gt;</c>. A bare entry matches only the system-directory reduction, which is why
/// a real prefix carries starless <c>api-ms-win-crt-*</c> entries — those DLLs live in system32 —
/// beside starred ones. Verified against the real EADesktop.exe: with the starred entry Wine logs
/// <c>got standard key n,b</c> and loads the app-directory DLL; without it, <c>got hardcoded
/// default</c> and the app-directory file is never loaded at all.
/// </summary>
public static class WineDllOverride
{
    public const string Key = @"Software\Wine\DllOverrides";
    public const string ValueName = "*version";
    public const string Native = "native,builtin";

    /// <summary>
    /// Whether the prefix's own registry already prefers the native DLL path-independently. A bare
    /// <c>"version"</c> entry does NOT satisfy this: per the load order it does not cover an
    /// app-directory DLL.
    /// </summary>
    public static bool IsSatisfied(WinePrefix prefix)
    {
        var value = WineRegistryFile.ReadValue(prefix.UserRegFile, Key, ValueName)?.Text;

        return value is { Length: > 0 }
               && LauncherOverrides.Classify($"*version={value}") == OverrideVerdict.SuppliesNative;
    }

    /// <param name="afterWrite">
    /// Test seam for the flush: wineserver saves on a timer and lingers
    /// after its last client, so a write can be discarded AFTER it succeeded. Production passes
    /// none.
    /// </param>
    /// <returns>Null on success; the user-facing failure otherwise.</returns>
    public static async Task<string?> ApplyAsync(WinePrefix prefix, WineOverrideStore store,
                                                 OverrideVerdict verdict, CancellationToken ct,
                                                 Action? afterWrite = null)
    {
        var existing = store.Read(prefix.Root);

        if (verdict == OverrideVerdict.SuppliesNative || IsSatisfied(prefix))
        {
            // A record's PriorValue/CreatedBlock are never overwritten by a later call: doing so
            // would let an ordinary reinstall record our own "native,builtin" as PriorValue, and
            // removal would then restore the override we just set — the unlocker keeps loading
            // after the user was told it was gone. Nothing to repair here either way, since the
            // requirement is already met.
            if (existing is null)
            {
                // The only record whose loss carries no consequence: nothing was written, so it
                // says "there is nothing to undo" and its absence says the same thing.
                await store.WriteAsync(
                    new WineOverrideRecord(prefix.Root, false, null, false,
                                           "the Wine DLL override was already in place"), ct);
            }

            return null;
        }

        // A prefix validates on system.reg and drive_c alone, so user.reg can be absent — a
        // Proton prefix mid-recreate, a partial restore, a user who deleted it to reset HKCU.
        // WineRegistryFile treats a missing file as an empty one, so writing here would create a
        // user.reg holding nothing but our own block, with no "WINE REGISTRY Version 2" header
        // and no #arch. Wine rejects such a hive, so the override would never load while the
        // confirming re-read parsed our own file and reported success. UndoAsync refuses the same
        // shape for the same reason.
        if (!File.Exists(prefix.UserRegFile))
        {
            return $"'{prefix.Root}' does not have a valid user.reg file; the prefix may be "
                   + "damaged. The Wine DLL override was not written.";
        }

        // Reached with an existing record when the "*version" entry the record describes is no
        // longer satisfied and nothing external supplies it — a launcher override the record
        // recorded as "already in place" was removed, or the entry itself was deleted from
        // user.reg. The value is repaired below, but the record keeps ITS OWN original PriorValue
        // and CreatedBlock rather than whatever this write reports: removal must still restore the
        // user's ORIGINAL value, not a value fabricated by this repair.
        WineRegistryWrite write;
        try
        {
            write = await WineRegistryFile.SetValueAsync(prefix.UserRegFile, Key, ValueName,
                                                         Native, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"'{prefix.Root}' cannot be written to: {e.Message}.";
        }

        afterWrite?.Invoke();

        // The value a removal must restore, and whether the key block was ours to remove. Taken
        // from the EXISTING record when there is one — a repair — never from this write's own
        // report, which on a repair reflects the state left after the entry vanished rather than
        // the user's original value.
        var priorValue = existing?.PriorValue ?? write.PriorValue;
        var createdBlock = existing?.CreatedBlock ?? write.CreatedBlock;

        // Re-read and confirm. Detection cannot see a wineserver lingering with no clients, and that
        // server owns the registry and rewrites user.reg when it saves. Read the file directly to
        // distinguish a transient read failure (which could be I/O noise but the write landed) from
        // a genuine flush that discarded the write. They require opposite handling: a lost write
        // leaves no record (so re-running rewrites), but a misread landing leaves a record (so
        // removal can undo).
        string? fileText;
        try
        {
            fileText = await File.ReadAllTextAsync(prefix.UserRegFile, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Read itself failed. The write landed but we cannot confirm. Record it so removal can
            // undo, but report the confirmation failure.
            var unconfirmed = $"The Wine DLL override was written but could not be confirmed (the "
                              + $"prefix is temporarily unavailable: {e.Message})";

            return await store.WriteAsync(new WineOverrideRecord(prefix.Root, true, priorValue,
                                                                createdBlock, null), ct) is { } save
                ? $"{unconfirmed}, and the record needed to undo it could not be saved ({save}). "
                  + "Removal will not clear it."
                : $"{unconfirmed}; removal will clear it.";
        }

        // Read succeeded. Parse the file text we already have to check if the value is there.
        // Use the lines-based parser to avoid a second disk read.
        var lines = fileText.Split('\n');
        var found = WineRegistryFile.ReadKeyFromLines(lines, Key) is { } values
                    && values.TryGetValue(ValueName, out var value)
                    && value.Text == Native;

        if (found)
        {
            // Value is present and correct: the write succeeded and survived. The record is what
            // makes it reversible, so a record that cannot be saved has to be reported — otherwise
            // removal reads nothing, does nothing and reports success, and the value the override
            // replaced is gone with no trace of what it was.
            if (await store.WriteAsync(new WineOverrideRecord(prefix.Root, true, priorValue,
                                                             createdBlock, null), ct) is { } save)
            {
                return "The Wine DLL override was set, but the record needed to undo it could not "
                       + $"be saved ({save}), so a removal will not restore the previous value.";
            }

            return null;
        }

        // Read succeeded but the value is absent or wrong: the write was genuinely lost to a flush.
        // Do not record it, so a re-run will attempt the write again.
        return "The Wine DLL override was written but did not survive; something is using "
               + "this prefix. Close it and try again.";
    }

    /// <returns>Null on success; the user-facing failure otherwise.</returns>
    public static async Task<string?> UndoAsync(WinePrefix prefix, WineOverrideStore store,
                                                CancellationToken ct)
    {
        if (store.Read(prefix.Root) is not { } record) return null;

        // Recorded as "nothing was written", so there is nothing of ours to undo. Touching the
        // registry here would clear an override the user or a launcher established.
        if (!record.WroteRegistry)
        {
            store.Delete(prefix.Root);
            return null;
        }

        // The prefix is gone: a Proton recreate or a deleted bottle. Only delete the record if the
        // root directory is actually gone. If it exists but user.reg is missing, that is a damaged
        // prefix and we should report it rather than attempt to repair it.
        if (!Directory.Exists(prefix.Root))
        {
            store.Delete(prefix.Root);
            return null;
        }

        if (!File.Exists(prefix.UserRegFile))
        {
            // The prefix root exists but user.reg is missing: the prefix is damaged. Keep the
            // record and report the issue rather than fabricating a new user.reg.
            return $"'{prefix.Root}' does not have a valid user.reg file; the prefix may be "
                   + "damaged. The unlocker override record is kept for manual recovery.";
        }

        try
        {
            if (record.PriorValue is { } prior)
            {
                await WineRegistryFile.SetValueAsync(prefix.UserRegFile, Key, ValueName, prior, ct);
            }
            else
            {
                await WineRegistryFile.RemoveValueAsync(prefix.UserRegFile, Key, ValueName,
                                                        record.CreatedBlock, ct);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"'{prefix.Root}' cannot be written to: {e.Message}.";
        }

        store.Delete(prefix.Root);
        return null;
    }

    /// <summary>
    /// Deletes every record whose prefix root directory no longer exists, reporting each — the
    /// same shape as the existing orphaned-partials sweep. Without it a record outlives its prefix
    /// and the next removal writes a recorded value into a registry that was regenerated in the
    /// meantime. If the root exists but validation fails (permission, I/O, format), reports and
    /// keeps the record rather than silently deleting it.
    /// </summary>
    public static IReadOnlyList<string> SweepOrphans(WineOverrideStore store, string userName)
    {
        var reported = new List<string>();

        foreach (var record in store.All())
        {
            // Only delete if the prefix root is gone. A root that exists but fails validation is
            // reported but kept, so the undo can try to repair it or the user can investigate.
            if (!Directory.Exists(record.PrefixPath))
            {
                // "Does not exist" is also the answer for a prefix on an unmounted volume, and a
                // Proton prefix under a second Steam library is an ordinary place to keep one.
                // Deleting the record on that answer alone loses the undo for a prefix that is
                // coming back, and the override then stays in the user's registry with nothing
                // left on disk that could remove it. The parent separates the two: a deleted
                // prefix leaves its parent behind, an absent mount takes the whole chain with it.
                var parent = Path.GetDirectoryName(record.PrefixPath);
                if (parent is null || !Directory.Exists(parent))
                {
                    reported.Add($"'{record.PrefixPath}' cannot be reached, so its unlocker "
                                 + "override record is kept.");
                    continue;
                }

                store.Delete(record.PrefixPath);
                reported.Add($"'{record.PrefixPath}' no longer exists.");
                continue;
            }

            // Root exists but may not be a valid prefix. Try to open it.
            if (WinePrefix.TryOpen(record.PrefixPath,
                                   new TargetEnvironment(EnvironmentSource.Wine, record.PrefixPath),
                                   userName) is null)
            {
                // Validation failed but the root exists. Keep the record and report the issue.
                reported.Add($"'{record.PrefixPath}' is not a valid Wine prefix.");
            }
        }

        return reported;
    }
}
