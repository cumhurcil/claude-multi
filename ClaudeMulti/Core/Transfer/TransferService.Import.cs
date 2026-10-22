using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeMulti.Core.Transfer;

public sealed partial class TransferService
{
    private static readonly string[] ReservedDeviceNames =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    private static readonly string[] CookieFiles = ["Network/Cookies", "Network/Cookies-journal"];

    private sealed class ImportState
    {
        public int Written;
        public int Kept;
        public long Bytes;
        public List<string> Warnings { get; } = [];
        public HashSet<string> Warned { get; } = [];

        public void WarnOnce(string key, string message)
        {
            if (Warned.Add(key)) Warnings.Add(message);
        }
    }

    /// <summary>
    /// Puts the selected kinds from a bundle onto this PC. Chats are merged (missing or longer entries are added,
    /// nothing newer here is overwritten, nothing is deleted); sign-ins and settings replace what is here. Every
    /// replaced file is copied to the backup folder first, and a failure rolls everything back.
    /// </summary>
    public ImportResult Import(string bundlePath, string passphrase, TransferScope requested, Action<string>? report = null)
    {
        using var bundle = TransferBundle.Open(bundlePath, passphrase);
        var manifest = bundle.Manifest;
        var scope = requested.Intersect(manifest.Scope);
        var skippedSignIns = scope.SignIns && !SupportsSignIns;
        if (skippedSignIns) scope = scope with { SignIns = false };
        if (!scope.Any) throw new TransferException(skippedSignIns ? SignInsWindowsOnly : "The bundle has no data of the kind you chose.");

        foreach (var instance in manifest.Instances)
            if (!IsValidInstanceId(instance.Id)) throw new TransferException($"Instance name in the bundle is invalid: '{instance.Id}'.");

        RequireClosed(manifest.Instances.Select(i => new InstanceInfo(i.Id, env.DataDirOf(i.Id))),
            "import changes files");

        var keys = new Dictionary<string, byte[]>();
        foreach (var instance in manifest.Instances.Where(i => !string.IsNullOrEmpty(i.Key)))
            keys[instance.Id] = DecodeKey(instance);

        var journal = new ImportJournal(Path.Combine(env.ImportBackupsDir, DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        var state = new ImportState();
        if (skippedSignIns) state.Warnings.Add(SignInsWindowsOnly);
        if (scope.Settings && manifest.Os.Length > 0 && manifest.Os != OsName(HostPlatform.Current))
            state.Warnings.Add($"The bundle was made on {manifest.Os}; settings such as MCP server commands may contain paths that do not exist here.");
        try
        {
            if (scope.SignIns) ClearSignInState(keys.Keys, journal);

            var configs = new Dictionary<string, ZipArchiveEntry>();
            var files = bundle.Zip.Entries.Where(e => e.FullName != ManifestEntry && !e.FullName.EndsWith('/')).ToList();
            for (var i = 0; i < files.Count; i++)
            {
                if (i % 250 == 0) report?.Invoke($"Importing… {i}/{files.Count} files");
                ImportEntry(files[i], scope, keys, journal, configs, state);
            }

            report?.Invoke("Applying settings and sign-ins…");
            MergeConfigs(configs, scope, keys, journal, state);
            if (scope.SignIns)
            {
                foreach (var (id, key) in keys)
                {
                    var localState = ChromiumKey.LocalStatePath(new InstanceInfo(id, env.DataDirOf(id)));
                    journal.BeforeWrite(localState);
                    ChromiumKey.Write(localState, key, protector);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or TransferException)
        {
            var failed = journal.Rollback();
            var outcome = failed.Count == 0
                ? "The changes made were rolled back."
                : $"{failed.Count} files could not be rolled back; the originals are here: {journal.BackupDir}";
            throw new TransferException($"{e.Message} {outcome}", e);
        }

        var labels = manifest.Instances
            .Where(i => !string.IsNullOrWhiteSpace(i.Label))
            .ToDictionary(i => i.Id, i => i.Label!);
        return new ImportResult(state.Written, state.Kept, state.Bytes, journal.BackupDir, labels, Capped(state.Warnings));
    }

    private void ImportEntry(
        ZipArchiveEntry entry, TransferScope scope, IReadOnlyDictionary<string, byte[]> keys, ImportJournal journal,
        Dictionary<string, ZipArchiveEntry> configs, ImportState state)
    {
        if (!TryResolve(entry.FullName, out var root, out var id, out var rel))
        {
            state.Warnings.Add($"Invalid path skipped: {entry.FullName}");
            return;
        }
        if (root == TransferRoots.Instances && rel == "config.json")
        {
            configs[id] = entry;
            return;
        }
        if (TransferCatalog.Classify(root, rel, isDirectory: false) is not { } kind || !scope.Includes(kind)) return;

        if (kind == TransferKind.SignIn && !SignInUsable(root, id, keys))
        {
            state.WarnOnce(root + id, $"{DisplayName(root == TransferRoots.Instances ? id : ClaudeEnvironment.MainId)}: the bundle has no encryption key; sign-in not imported.");
            return;
        }
        var target = TargetPath(root, id, rel);
        if (target == null)
        {
            state.Warnings.Add($"Path escaping the target folder skipped: {entry.FullName}");
            return;
        }
        if (kind == TransferKind.Chats && File.Exists(target) && !IsBetterChatCopy(entry, rel, target))
        {
            state.Kept++;
            return;
        }

        journal.BeforeWrite(target);
        Extract(entry, target);
        state.Written++;
        state.Bytes += entry.Length;
    }

    /// <summary>Sign-in files only make sense with the key they were encrypted with. Plain files of Claude Code need none.</summary>
    private static bool SignInUsable(string root, string id, IReadOnlyDictionary<string, byte[]> keys) => root switch
    {
        TransferRoots.Instances => keys.ContainsKey(id),
        TransferRoots.Switcher => keys.ContainsKey(ClaudeEnvironment.MainId),
        _ => true,
    };

    /// <summary>
    /// Stale web sessions would clash with the incoming account, so the target's cookie store and web storage are
    /// cleared for every copy whose sign-in is replaced. They are backed up first.
    /// </summary>
    private void ClearSignInState(IEnumerable<string> instanceIds, ImportJournal journal)
    {
        foreach (var id in instanceIds)
        {
            var dir = env.DataDirOf(id);
            foreach (var name in new[] { "Local Storage", "Session Storage", "IndexedDB", "Partitions", "WebStorage" })
                journal.ReplaceDirectory(Path.Combine(dir, name));
            foreach (var cookie in CookieFiles)
                journal.Remove(Path.Combine(dir, cookie.Replace('/', Path.DirectorySeparatorChar)));
        }
    }

    private void MergeConfigs(
        Dictionary<string, ZipArchiveEntry> configs, TransferScope scope, IReadOnlyDictionary<string, byte[]> keys,
        ImportJournal journal, ImportState state)
    {
        foreach (var (id, entry) in configs)
        {
            JsonObject? incoming;
            using (var stream = entry.Open()) incoming = JsonNode.Parse(stream) as JsonObject;
            if (incoming == null) continue;

            // Tokens are only taken together with their key; without it they could never be decrypted here.
            var signIn = scope.SignIns && keys.ContainsKey(id);
            if (scope.SignIns && !signIn)
                state.WarnOnce("config" + id, $"{DisplayName(id)}: the bundle has no encryption key; sign-in not imported.");
            var selected = FilterConfig(incoming, new TransferScope(signIn, false, scope.Settings));
            if (selected.Count == 0 && !signIn) continue;

            var path = Path.Combine(env.DataDirOf(id), "config.json");
            var (status, config) = JsonFile.Read(path);
            if (status == DocState.Invalid)
                throw new TransferException($"{DisplayName(id)}: could not read config.json (broken); not changed.");

            var target = config ?? new JsonObject();
            if (signIn)
            {
                // A sign-in the source no longer has must not survive here as a mix of two accounts.
                foreach (var stale in target.Select(p => p.Key).Where(k => SettingsSync.IsAuthKey(k) && !selected.ContainsKey(k)).ToList())
                    target.Remove(stale);
            }
            foreach (var (key, value) in selected) target[key] = value?.DeepClone();

            journal.BeforeWrite(path);
            JsonFile.Write(path, target);
            state.Written++;
        }
    }

    // ---- paths ----

    private static bool IsValidInstanceId(string id) =>
        id == ClaudeEnvironment.MainId || InstanceCatalog.ValidateName(id) == null;

    private static byte[] DecodeKey(ManifestInstance instance)
    {
        try
        {
            var key = Convert.FromBase64String(instance.Key!);
            if (key.Length == TransferCrypto.KeySize || key.Length == 16) return key;
        }
        catch (FormatException) { /* reported below */ }
        throw new TransferException($"{DisplayName(instance.Id)}: the encryption key in the bundle is invalid.");
    }

    /// <summary>Splits an entry name into root, instance id and relative path, refusing anything that is not a plain relative path.</summary>
    private static bool TryResolve(string name, out string root, out string id, out string rel)
    {
        root = id = rel = "";
        var parts = name.Split('/');
        if (parts.Length < 2 || !parts.All(IsSafeSegment)) return false;
        root = parts[0];
        switch (root)
        {
            case TransferRoots.Instances:
                if (parts.Length < 3 || !IsValidInstanceId(parts[1])) return false;
                id = parts[1];
                rel = string.Join('/', parts[2..]);
                return true;
            case TransferRoots.ClaudeHome:
            case TransferRoots.Profile:
            case TransferRoots.Switcher:
                rel = string.Join('/', parts[1..]);
                return true;
            default:
                return false;
        }
    }

    private static bool IsSafeSegment(string segment)
    {
        if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')) return false;
        if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        var stem = segment.Split('.')[0].TrimEnd();
        return !ReservedDeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Where an entry is written, or null when it would land outside its root folder.</summary>
    private string? TargetPath(string root, string id, string rel)
    {
        var baseDir = root switch
        {
            TransferRoots.Instances => env.DataDirOf(id),
            TransferRoots.ClaudeHome => env.ClaudeHomeDir,
            TransferRoots.Profile => env.Home,
            TransferRoots.Switcher => env.SwitcherDir,
            _ => null,
        };
        if (baseDir == null) return null;

        var prefix = Path.GetFullPath(baseDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(baseDir, rel.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(prefix, HostPlatform.PathComparison()) ? full : null;
    }

    // ---- files ----

    /// <summary>
    /// For chats that exist on both PCs: the one with later activity wins for chat-list entries, the longer one for
    /// conversation logs (they are append-only). The copy here is kept when in doubt.
    /// </summary>
    private static bool IsBetterChatCopy(ZipArchiveEntry entry, string rel, string target)
    {
        var name = Path.GetFileName(rel);
        if (rel.StartsWith("claude-code-sessions/", StringComparison.Ordinal) &&
            name.StartsWith("local_", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
            return EntryActivity(entry) > ChatSync.LastActivity(target);
        return entry.Length > new FileInfo(target).Length;
    }

    private static long EntryActivity(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.TryGetProperty("lastActivityAt", out var p) && p.TryGetInt64(out var t) ? t : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static void Extract(ZipArchiveEntry entry, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".manager-tmp";
        using (var source = entry.Open())
        using (var destination = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            source.CopyTo(destination);
        File.SetLastWriteTimeUtc(temp, entry.LastWriteTime.UtcDateTime);
        ImportJournal.ClearReadOnly(target);
        File.Move(temp, target, overwrite: true);
    }
}
