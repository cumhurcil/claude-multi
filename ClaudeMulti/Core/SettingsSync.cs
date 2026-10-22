using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeMulti.Core;

public sealed record SettingsFile(string Name, Func<string, bool> IsSynced);

public sealed record SettingsSyncResult(int KeysApplied, int FilesWritten, int PendingInstances);

/// <summary>
/// Keeps app settings equal across instances without touching sign-in data.
/// Per key, the value changed most recently since the last sync wins. A running app is never
/// written to (it would overwrite the file on exit); it catches up once it is closed.
/// </summary>
public static partial class SettingsSync
{
    // Sign-in tokens, account ids and machine-specific state stay per instance.
    [GeneratedRegex(@"^(oauth:|lastKnownAccountUuid$|first_launch_at$|version_first_launch$|updater|quickWindowPosition$|windowControls|windowSize|boot|planUsage|remote_.*migration|hasTrackedInitial|lastSeen|hostGit|dxt:allowlist)")]
    private static partial Regex NeverSynced();

    /// <summary>A config.json entry that belongs to the signed-in account (its tokens), not to the app.</summary>
    public static bool IsAuthKey(string key) =>
        key.StartsWith("oauth:", StringComparison.Ordinal) || key == "lastKnownAccountUuid";

    /// <summary>A config.json entry that is a user setting: safe to carry to another instance or PC.</summary>
    public static bool IsPortableKey(string key) => !NeverSynced().IsMatch(key);

    public static readonly IReadOnlyList<SettingsFile> Files =
    [
        new("config.json", key => !NeverSynced().IsMatch(key)),
        new("claude_desktop_config.json", _ => true),
    ];

    private const int KeptBackups = 20;

    private sealed record Doc(InstanceInfo Instance, string Path, DocState State, JsonObject? Obj, DateTime Modified);

    public static SettingsSyncResult Run(
        IReadOnlyList<InstanceInfo> instances,
        Func<InstanceInfo, bool> isRunning,
        string recordPath,
        string backupsDir,
        DateTime nowUtc)
    {
        var records = SyncRecords.Load(recordPath);
        var running = instances.ToDictionary(i => i.Id, isRunning);
        var pending = new HashSet<string>();
        int applied = 0, written = 0;

        foreach (var file in Files)
        {
            var docs = instances.Select(i => ReadDoc(i, file)).ToDictionary(d => d.Instance.Id);
            var writes = PlanWrites(file, docs, running, records, pending);
            foreach (var (id, changes) in writes)
            {
                var doc = docs[id];
                Backup(doc, backupsDir, nowUtc);
                var target = doc.Obj ?? new JsonObject();
                foreach (var (key, value) in changes) target[key] = value;
                JsonFile.Write(doc.Path, target);
                applied += changes.Count;
                written++;
            }
        }

        records.Save(recordPath);
        PruneBackups(backupsDir);
        return new SettingsSyncResult(applied, written, pending.Count);
    }

    private static Doc ReadDoc(InstanceInfo instance, SettingsFile file)
    {
        var path = Path.Combine(instance.DataDir, file.Name);
        var (state, obj) = JsonFile.Read(path);
        var modified = state == DocState.Ok ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        return new Doc(instance, path, state, obj, modified);
    }

    private static Dictionary<string, Dictionary<string, JsonNode?>> PlanWrites(
        SettingsFile file,
        Dictionary<string, Doc> docs,
        Dictionary<string, bool> running,
        SyncRecords records,
        HashSet<string> pending)
    {
        var writes = new Dictionary<string, Dictionary<string, JsonNode?>>();
        var keys = docs.Values.Where(d => d.State == DocState.Ok)
            .SelectMany(d => d.Obj!.Select(p => p.Key))
            .Where(file.IsSynced)
            .Distinct()
            .ToList();

        foreach (var key in keys)
        {
            var holders = docs.Values.Where(d => d.Obj?.ContainsKey(key) == true).ToList();
            var changed = holders
                .Where(d => SyncRecords.Hash(JsonFile.Canon(d.Obj![key])) != records.Get(file.Name, d.Instance.Id, key))
                .ToList();
            var winner = (changed.Count > 0 ? changed : holders).MaxBy(d => d.Modified)!;
            var winnerCanon = JsonFile.Canon(winner.Obj![key]);
            var winnerHash = SyncRecords.Hash(winnerCanon);

            foreach (var doc in docs.Values)
            {
                var id = doc.Instance.Id;
                var current = doc.Obj?.ContainsKey(key) == true ? JsonFile.Canon(doc.Obj[key]) : null;
                if (current == winnerCanon)
                {
                    records.Set(file.Name, id, key, winnerHash);
                    continue;
                }
                if (doc.State == DocState.Invalid || running[id])
                {
                    // Only observed: it keeps its value until it is closed, then catches up.
                    records.Set(file.Name, id, key, current == null ? null : SyncRecords.Hash(current));
                    pending.Add(id);
                    continue;
                }
                if (!writes.TryGetValue(id, out var changes)) writes[id] = changes = new();
                changes[key] = JsonNode.Parse(winnerCanon);
                records.Set(file.Name, id, key, winnerHash);
            }
        }
        return writes;
    }

    private static void Backup(Doc doc, string backupsDir, DateTime nowUtc)
    {
        if (doc.State != DocState.Ok) return;
        var dir = Path.Combine(backupsDir, nowUtc.ToString("yyyyMMdd-HHmmss"), doc.Instance.Id);
        Directory.CreateDirectory(dir);
        File.Copy(doc.Path, Path.Combine(dir, Path.GetFileName(doc.Path)), overwrite: true);
    }

    private static void PruneBackups(string backupsDir)
    {
        if (!Directory.Exists(backupsDir)) return;
        var old = Directory.GetDirectories(backupsDir).OrderByDescending(d => d, StringComparer.Ordinal).Skip(KeptBackups);
        foreach (var dir in old)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* a backup that cannot be pruned now is pruned next time */ }
        }
    }
}
