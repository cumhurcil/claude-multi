using System.Text.Json;

namespace ClaudeMulti.Core;

/// <summary>
/// Makes every chat entry visible in every account folder of every instance.
/// Entries are small <c>local_&lt;id&gt;.json</c> files; the conversations themselves live in
/// <c>~/.claude/projects</c> and are shared already. Only adds and refreshes, never deletes,
/// and never brings back a chat marked <c>deleted_&lt;id&gt;</c>.
/// </summary>
public static class ChatSync
{
    private const string LocalPrefix = "local_";
    private const string DeletedPrefix = "deleted_";

    private sealed record Best(long Time, string Path, string Name);

    public static int Run(IReadOnlyList<InstanceInfo> instances)
    {
        EnsureTargets(instances);
        var dirs = instances.SelectMany(i => InstanceProbe.SessionDirs(i)).ToList();
        if (dirs.Count < 2) return 0;

        var deleted = CollectDeleted(dirs);
        var best = PickNewest(dirs, deleted);

        var written = 0;
        foreach (var dir in dirs)
        {
            foreach (var entry in best.Values)
            {
                var target = Path.Combine(dir, entry.Name);
                if (string.Equals(target, entry.Path, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(target) && LastActivity(target) >= entry.Time) continue;
                Copy(entry.Path, target);
                written++;
            }
        }
        return written;
    }

    /// <summary>
    /// Creates the signed-in account's folder for every known organization, so an instance
    /// that just signed in sees all chats on its next start.
    /// </summary>
    private static void EnsureTargets(IReadOnlyList<InstanceInfo> instances)
    {
        var orgs = instances.SelectMany(i => InstanceProbe.SessionDirs(i))
            .Select(Path.GetFileName)
            .Where(InstanceProbe.IsSafeSegment)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var instance in instances)
        {
            var account = InstanceProbe.SignedInAccount(instance);
            if (account == null) continue;
            foreach (var org in orgs)
                Directory.CreateDirectory(Path.Combine(instance.SessionsDir, account, org!));
        }
    }

    private static HashSet<string> CollectDeleted(IEnumerable<string> dirs)
    {
        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
            foreach (var file in Directory.EnumerateFiles(dir, DeletedPrefix + "*"))
                deleted.Add(Path.GetFileName(file)[DeletedPrefix.Length..]);
        return deleted;
    }

    private static Dictionary<string, Best> PickNewest(IEnumerable<string> dirs, HashSet<string> deleted)
    {
        var best = new Dictionary<string, Best>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            foreach (var file in Directory.EnumerateFiles(dir, LocalPrefix + "*.json"))
            {
                var name = Path.GetFileName(file);
                var id = name[LocalPrefix.Length..^".json".Length];
                if (deleted.Contains(id)) continue;
                var time = LastActivity(file);
                if (!best.TryGetValue(id, out var current) || time > current.Time)
                    best[id] = new Best(time, file, name);
            }
        }
        return best;
    }

    internal static long LastActivity(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            return doc.RootElement.TryGetProperty("lastActivityAt", out var p) && p.TryGetInt64(out var t) ? t : 0;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void Copy(string source, string target)
    {
        var tmp = target + ".manager-tmp";
        File.Copy(source, tmp, overwrite: true);
        File.SetLastWriteTimeUtc(tmp, File.GetLastWriteTimeUtc(source));
        File.Move(tmp, target, overwrite: true);
    }
}
