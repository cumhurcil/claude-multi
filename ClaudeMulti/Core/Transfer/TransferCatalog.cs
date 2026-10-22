namespace ClaudeMulti.Core.Transfer;

public enum TransferKind { SignIn, Chats, Settings }

/// <summary>Which kinds of data a transfer carries.</summary>
public sealed record TransferScope(bool SignIns, bool Chats, bool Settings)
{
    public static readonly TransferScope All = new(true, true, true);

    public bool Any => SignIns || Chats || Settings;

    public bool Includes(TransferKind kind) => kind switch
    {
        TransferKind.SignIn => SignIns,
        TransferKind.Chats => Chats,
        TransferKind.Settings => Settings,
        _ => false,
    };

    public TransferScope Intersect(TransferScope other) =>
        new(SignIns && other.SignIns, Chats && other.Chats, Settings && other.Settings);
}

/// <summary>Where an entry of the bundle comes from. The first path segment of its name.</summary>
public static class TransferRoots
{
    /// <summary><c>instances/&lt;id&gt;/…</c>: the data folder of the main Claude or an extra copy.</summary>
    public const string Instances = "instances";
    /// <summary><c>claude-home/…</c>: Claude Code's folder, ~/.claude.</summary>
    public const string ClaudeHome = "claude-home";
    /// <summary><c>profile/.claude.json</c>: Claude Code's user-level file next to ~/.claude.</summary>
    public const string Profile = "profile";
    /// <summary><c>switcher/…</c>: the saved sign-ins of the tray account switcher.</summary>
    public const string Switcher = "switcher";
}

/// <summary>One file that goes into (or comes out of) the bundle.</summary>
public sealed record TransferEntry(string Name, string FullPath, TransferKind Kind, long Length, DateTime ModifiedUtc);

/// <summary>
/// Decides what is worth moving. Only well-understood files are taken: caches, logs, downloaded binaries and
/// machine-bound state stay behind, and anything not listed is left alone rather than guessed at. Import runs
/// every entry of a bundle through the same rules, so a bundle cannot write outside them.
/// </summary>
public static class TransferCatalog
{
    // Chromium keeps these encrypted with the app key; they are replaced as a whole, never merged.
    private static readonly string[] WebStorage = ["Local Storage", "Session Storage", "IndexedDB", "Partitions", "WebStorage"];

    public static bool IsWebStorageDir(string name) => WebStorage.Contains(name, StringComparer.Ordinal);

    // Folders of ~/.claude that hold chats; everything else in it is settings unless listed in HomeSkipped.
    private static readonly string[] HomeChats = ["projects", "history.jsonl", "todos", "file-history", "plans"];

    private static readonly string[] HomeSkipped =
    [
        "cache", "downloads", "paste-cache", "image-cache", "shell-snapshots", "session-env", "session-data",
        "sessions", "state", "statsig", "telemetry", "debug", "ide", "backups", "local", "tasks",
        ".last-cleanup", "lockfile",
    ];

    /// <summary>What kind of data a path is, or null when it must not be moved. <paramref name="rel"/> uses '/'.</summary>
    public static TransferKind? Classify(string root, string rel, bool isDirectory)
    {
        if (rel.Length == 0) return null;
        var parts = rel.Split('/');
        if (!isDirectory && IsScratchFile(parts[^1])) return null;

        return root switch
        {
            TransferRoots.Instances => InstanceKind(parts),
            TransferRoots.ClaudeHome => HomeKind(parts),
            TransferRoots.Profile => parts is [".claude.json"] ? TransferKind.Settings : null,
            TransferRoots.Switcher => parts[0] is "state.json" or "accounts" ? TransferKind.SignIn : null,
            _ => null,
        };
    }

    private static bool IsScratchFile(string name) =>
        name.EndsWith(".manager-tmp", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".switcher-tmp", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    private static TransferKind? InstanceKind(string[] parts)
    {
        switch (parts[0])
        {
            case "claude-code-sessions":
            case "local-agent-mode-sessions":
                return TransferKind.Chats;
            case "Local Storage":
            case "Session Storage":
            case "IndexedDB":
            case "Partitions":
            case "WebStorage":
                return TransferKind.SignIn;
            case "Network": // only the cookie store; the rest is caches and network state of this PC
                return parts.Length == 1 || parts is [_, "Cookies" or "Cookies-journal"] ? TransferKind.SignIn : null;
            case "claude_desktop_config.json":
            case "Preferences":
                return parts.Length == 1 ? TransferKind.Settings : null;
            case "Claude Extensions":
            case "Claude Extensions Settings":
                return TransferKind.Settings;
            default: // config.json and "Local State" are handled by the service, not copied as files
                return null;
        }
    }

    private static TransferKind? HomeKind(string[] parts)
    {
        var first = parts[0];
        if (HomeSkipped.Contains(first, StringComparer.OrdinalIgnoreCase)) return null;
        if (first.Contains(".bak", StringComparison.OrdinalIgnoreCase)) return null;
        if (first == ".credentials.json") return parts.Length == 1 ? TransferKind.SignIn : null;
        return HomeChats.Contains(first, StringComparer.OrdinalIgnoreCase) ? TransferKind.Chats : TransferKind.Settings;
    }

    /// <summary>Every file of the selected kinds on this PC, with its entry name in the bundle.</summary>
    public static IEnumerable<TransferEntry> Enumerate(
        ClaudeEnvironment env, IReadOnlyList<InstanceInfo> instances, TransferScope scope, List<string> warnings)
    {
        foreach (var instance in instances)
            foreach (var entry in Walk(TransferRoots.Instances, $"{TransferRoots.Instances}/{instance.Id}", instance.DataDir, "", scope, warnings))
                yield return entry;

        foreach (var entry in Walk(TransferRoots.ClaudeHome, TransferRoots.ClaudeHome, env.ClaudeHomeDir, "", scope, warnings))
            yield return entry;

        if (scope.Includes(TransferKind.Settings) && File.Exists(env.ClaudeJsonPath))
        {
            var info = new FileInfo(env.ClaudeJsonPath);
            yield return new TransferEntry($"{TransferRoots.Profile}/.claude.json", info.FullName, TransferKind.Settings, info.Length, info.LastWriteTimeUtc);
        }

        foreach (var entry in Walk(TransferRoots.Switcher, TransferRoots.Switcher, env.SwitcherDir, "", scope, warnings))
            yield return entry;
    }

    private static IEnumerable<TransferEntry> Walk(
        string root, string namePrefix, string dir, string rel, TransferScope scope, List<string> warnings)
    {
        if (!Directory.Exists(dir)) yield break;

        List<FileSystemInfo> children;
        try { children = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not read folder, skipped: {dir}");
            yield break;
        }

        foreach (var child in children)
        {
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue; // links could loop or leave the folder
            var childRel = rel.Length == 0 ? child.Name : $"{rel}/{child.Name}";
            var isDirectory = child is DirectoryInfo;
            if (Classify(root, childRel, isDirectory) is not { } kind || !scope.Includes(kind)) continue;

            if (isDirectory)
            {
                foreach (var entry in Walk(root, namePrefix, child.FullName, childRel, scope, warnings))
                    yield return entry;
            }
            else
            {
                var file = (FileInfo)child;
                yield return new TransferEntry($"{namePrefix}/{childRel}", file.FullName, kind, file.Length, file.LastWriteTimeUtc);
            }
        }
    }
}
