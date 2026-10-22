using System.Diagnostics;

namespace ClaudeMulti.Core;

public static class InstanceLauncher
{
    /// <summary>Starts (or focuses) an instance, optionally handing it a claude:// link.</summary>
    public static void Start(ClaudeEnvironment env, InstanceInfo instance, string? uri = null)
    {
        if (!env.CanLaunch)
            throw new InvalidOperationException(HostPlatform.Current == HostOs.Linux
                ? "Claude Desktop not found. Anthropic ships no Claude Desktop for Linux; if you use a community build, put its launcher on PATH as 'claude-desktop' or set CLAUDEMULTI_CLAUDE_EXE to its executable."
                : "Claude Desktop not found. Make sure Claude Desktop is installed.");
        if (uri != null && !ProtocolRouter.IsClaudeUri(uri))
            throw new ArgumentException("Invalid claude:// link.", nameof(uri));

        var args = new List<string>();
        if (!instance.IsMain) args.Add($"--user-data-dir={instance.DataDir}");
        if (uri != null) args.Add(uri);

        if (env.ClaudeExe == null)
        {
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Claude Desktop not found.");
            StoreApp.Launch(env.StoreAppId!, args);
            return;
        }

        Process.Start(BuildStartInfo(env.ClaudeExe, args, HostPlatform.Current))?.Dispose();
    }

    /// <summary>
    /// Windows starts the exe directly. On macOS and Linux it is started through <c>nohup</c> so it keeps running
    /// when the manager or its terminal closes, with its output thrown away. On macOS the binary inside Claude.app
    /// is run directly: <c>open -a Claude</c> would only bring the already running copy to the front.
    /// </summary>
    public static ProcessStartInfo BuildStartInfo(string exe, IReadOnlyList<string> args, HostOs os)
    {
        if (os == HostOs.Windows)
        {
            var direct = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var arg in args) direct.ArgumentList.Add(arg);
            return direct;
        }

        var info = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("nohup \"$0\" \"$@\" >/dev/null 2>&1 </dev/null &");
        info.ArgumentList.Add(exe);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    /// <summary>Delivers a browser sign-in link to the instance that is signing in.</summary>
    public static InstanceInfo? ForwardUri(ClaudeEnvironment env, StateStore store, string uri)
    {
        if (!ProtocolRouter.IsClaudeUri(uri)) return null;
        var state = store.Load();
        var target = ProtocolRouter.ResolveTarget(state, InstanceCatalog.List(env), DateTimeOffset.UtcNow);
        if (target == null) return null;
        store.Update(s => { s.PendingLoginId = null; s.PendingSince = null; });
        Start(env, target, uri);
        return target;
    }

    public static void MarkLoginPending(StateStore store, InstanceInfo instance) =>
        store.Update(s => { s.PendingLoginId = instance.Id; s.PendingSince = DateTimeOffset.UtcNow; });
}
