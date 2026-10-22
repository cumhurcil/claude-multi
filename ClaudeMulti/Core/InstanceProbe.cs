using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ClaudeMulti.Core;

/// <summary>Read-only questions about an instance's current state.</summary>
public static partial class InstanceProbe
{
    [GeneratedRegex("^[0-9A-Za-z-]{8,64}$")]
    private static partial Regex SafeSegment();

    /// <summary>True when a path segment from a config file is safe to use as a folder name.</summary>
    public static bool IsSafeSegment(string? s) => s != null && SafeSegment().IsMatch(s);

    /// <summary>
    /// Windows: Chromium keeps <c>lockfile</c> open exclusively while the app runs. macOS and Linux: it leaves a
    /// <c>SingletonLock</c> symlink pointing at "host-pid", which only counts while that process is alive.
    /// </summary>
    public static bool IsRunning(InstanceInfo instance) =>
        HostPlatform.IsWindows ? IsLockfileHeld(instance.LockPath) : IsSingletonOwnerAlive(instance.SingletonLockPath);

    private static bool IsLockfileHeld(string lockPath)
    {
        try
        {
            using var fs = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>The process id in a <c>SingletonLock</c> link target such as "my-laptop-4242", or null.</summary>
    public static int? SingletonOwnerPid(string? linkTarget)
    {
        if (string.IsNullOrEmpty(linkTarget)) return null;
        var dash = linkTarget.LastIndexOf('-');
        return dash > 0 && int.TryParse(linkTarget[(dash + 1)..], out var pid) && pid > 0 ? pid : null;
    }

    private static bool IsSingletonOwnerAlive(string lockPath)
    {
        string? target;
        try { target = new FileInfo(lockPath).LinkTarget; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }

        if (SingletonOwnerPid(target) is not { } pid) return false;
        try
        {
            using var owner = Process.GetProcessById(pid);
            return !owner.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false; // a stale link left by a crash
        }
    }

    public static string? SignedInAccount(InstanceInfo instance)
    {
        var id = JsonFile.ReadOrNull(instance.ConfigPath)?["lastKnownAccountUuid"]?.GetValue<string>();
        return IsSafeSegment(id) ? id : null;
    }

    public static bool IsSignedIn(InstanceInfo instance)
    {
        var token = JsonFile.ReadOrNull(instance.ConfigPath)?["oauth:tokenCacheV2"];
        return token != null && !string.IsNullOrEmpty(token.ToString());
    }

    /// <summary>Folders holding chat entries: sessions/&lt;account&gt;/&lt;organization&gt;.</summary>
    public static IEnumerable<string> SessionDirs(InstanceInfo instance, string? account = null)
    {
        if (!Directory.Exists(instance.SessionsDir)) yield break;
        var accounts = account != null
            ? new[] { Path.Combine(instance.SessionsDir, account) }
            : Directory.GetDirectories(instance.SessionsDir);
        foreach (var acc in accounts)
        {
            if (!Directory.Exists(acc)) continue;
            foreach (var org in Directory.GetDirectories(acc)) yield return org;
        }
    }

    /// <summary>Number of distinct chats the instance's signed-in account would show.</summary>
    public static int ChatCount(InstanceInfo instance)
    {
        var account = SignedInAccount(instance);
        if (account == null) return 0;
        return SessionDirs(instance, account)
            .SelectMany(d => Directory.EnumerateFiles(d, "local_*.json"))
            .Select(Path.GetFileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }
}
