using System.Text.RegularExpressions;

namespace ClaudeMulti.Core;

/// <summary>One Claude Desktop data folder: the main app or an extra instance.</summary>
public sealed record InstanceInfo(string Id, string DataDir)
{
    public bool IsMain => Id == ClaudeEnvironment.MainId;
    public string ConfigPath => Path.Combine(DataDir, "config.json");
    public string SessionsDir => Path.Combine(DataDir, "claude-code-sessions");

    /// <summary>True when the instance runs Claude Code on an API key instead of Claude Desktop (see <see cref="ApiProfile"/>).</summary>
    public bool IsApi => !IsMain && ApiProfile.IsConfigured(this);

    /// <summary>Windows: Chromium holds this file open exclusively while the app runs.</summary>
    public string LockPath => Path.Combine(DataDir, "lockfile");

    /// <summary>macOS and Linux: a symlink to "host-pid" of the running process.</summary>
    public string SingletonLockPath => Path.Combine(DataDir, "SingletonLock");
}

public static partial class InstanceCatalog
{
    private static readonly string[] Reserved = ["main", "backups", "import-backups", "con", "prn", "aux", "nul"];

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,31}$")]
    private static partial Regex NamePattern();

    /// <summary>The main instance first, then every extra instance folder by name.</summary>
    public static IReadOnlyList<InstanceInfo> List(ClaudeEnvironment env)
    {
        var result = new List<InstanceInfo>();
        if (Directory.Exists(env.MainDataDir))
            result.Add(new InstanceInfo(ClaudeEnvironment.MainId, env.MainDataDir));

        if (!Directory.Exists(env.InstancesRoot)) return result;
        foreach (var dir in Directory.GetDirectories(env.InstancesRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            if (ValidateName(name) == null && LooksLikeInstance(dir))
                result.Add(new InstanceInfo(name, dir));
        }
        return result;
    }

    /// <summary>Returns an error message, or null when the name can be used for a new instance.</summary>
    public static string? ValidateName(string name)
    {
        if (!NamePattern().IsMatch(name))
            return "The name must be 1-32 characters; letters, digits, - and _ are allowed.";
        if (Reserved.Contains(name, StringComparer.OrdinalIgnoreCase))
            return "This name is reserved, choose another.";
        return null;
    }

    public static InstanceInfo Create(ClaudeEnvironment env, string name)
    {
        var error = ValidateName(name);
        if (error != null) throw new ArgumentException(error, nameof(name));
        var dir = env.DataDirOf(name);
        if (Directory.Exists(dir) && LooksLikeInstance(dir))
            throw new InvalidOperationException($"An instance named '{name}' already exists.");
        Directory.CreateDirectory(Path.Combine(dir, "claude-code-sessions"));
        return new InstanceInfo(name, dir);
    }

    /// <summary>Deletes an extra instance's data folder. Never touches the main instance.</summary>
    public static void Remove(ClaudeEnvironment env, InstanceInfo instance)
    {
        if (instance.IsMain) throw new InvalidOperationException("The main Claude cannot be removed.");
        if (InstanceProbe.IsRunning(instance)) throw new InvalidOperationException("Close this instance first.");

        var root = Path.GetFullPath(env.InstancesRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(instance.DataDir);
        if (!target.StartsWith(root, HostPlatform.PathComparison()) || target.Length == root.Length)
            throw new InvalidOperationException("The folder is outside the instances folder; not deleted.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }

    private static bool LooksLikeInstance(string dir) =>
        File.Exists(Path.Combine(dir, "config.json")) || Directory.Exists(Path.Combine(dir, "claude-code-sessions"));
}
