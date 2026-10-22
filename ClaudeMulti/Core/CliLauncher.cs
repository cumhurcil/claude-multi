using System.Diagnostics;

namespace ClaudeMulti.Core;

/// <summary>Starts Claude Code (the CLI) for an API-key instance in its own console window.</summary>
public static class CliLauncher
{
    /// <summary>The argument that makes this program act as the console session of an API instance (see <see cref="CliSession"/>).</summary>
    public const string RunCliFlag = "--run-cli";

    private static readonly string[] WindowsNames = ["claude.exe", "claude.cmd"];
    private static readonly string[] UnixNames = ["claude"];

    /// <summary>The Claude Code executable: PATH first, then the native installer's and npm's usual places.</summary>
    public static string? FindCli(string home, string? pathVariable, string? appData) =>
        FindCli(home, pathVariable, appData, HostPlatform.Current);

    public static string? FindCli(string home, string? pathVariable, string? appData, HostOs os)
    {
        var names = os == HostOs.Windows ? WindowsNames : UnixNames;
        foreach (var dir in (pathVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim('"'), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { } // malformed PATH entry
            }
        }

        if (os != HostOs.Windows)
        {
            // A program started from the Dock or a menu does not get the shell's PATH, so look where installers put it.
            return UnixInstallDirs(home).Select(dir => Path.Combine(dir, "claude")).FirstOrDefault(File.Exists);
        }

        var native = Path.Combine(home, ".local", "bin", "claude.exe");
        if (File.Exists(native)) return native;
        var npm = appData == null ? null : Path.Combine(appData, "npm", "claude.cmd");
        return npm != null && File.Exists(npm) ? npm : null;
    }

    private static string[] UnixInstallDirs(string home) =>
    [
        Path.Combine(home, ".local", "bin"),
        Path.Combine(home, ".claude", "local"),
        Path.Combine(home, ".npm-global", "bin"),
        "/opt/homebrew/bin",
        "/usr/local/bin",
    ];

    public static string? FindCli(ClaudeEnvironment env) =>
        FindCli(env.Home, Environment.GetEnvironmentVariable("PATH"), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    private static string WorkDirOf(ApiSettings settings, string home) =>
        settings.WorkDir.Length > 0 && Directory.Exists(settings.WorkDir) ? settings.WorkDir : home;

    /// <summary>The PowerShell process to start (Windows). The window stays open after Claude Code exits so errors can be read.</summary>
    public static ProcessStartInfo BuildStartInfo(string cli, InstanceInfo instance, ApiSettings settings, string home)
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = WorkDirOf(settings, home),
        };
        // -NoExit keeps the window open after Claude Code exits; the path is a single-quoted PowerShell literal.
        var command = $"& '{cli.Replace("'", "''")}'" + (settings.SkipPermissions ? " --dangerously-skip-permissions" : "");
        info.Arguments = $"-NoLogo -NoExit -Command \"{command}\"";
        ApplyEnvironment(info, instance, settings);
        return info;
    }

    /// <summary>The process that runs Claude Code itself, with the instance's gateway settings in its environment.</summary>
    public static ProcessStartInfo BuildCliInfo(string cli, InstanceInfo instance, ApiSettings settings, string home)
    {
        var info = new ProcessStartInfo(cli) { UseShellExecute = false, WorkingDirectory = WorkDirOf(settings, home) };
        if (settings.SkipPermissions) info.ArgumentList.Add("--dangerously-skip-permissions");
        ApplyEnvironment(info, instance, settings);
        return info;
    }

    private static void ApplyEnvironment(ProcessStartInfo info, InstanceInfo instance, ApiSettings settings)
    {
        foreach (var (name, value) in ApiProfile.CliEnvironment(instance, settings))
            info.Environment[name] = value;
        Directory.CreateDirectory(ApiProfile.ClaudeConfigDirOf(instance));
    }

    // ---- macOS and Linux: a terminal window that runs "<this program> --run-cli <id>" ----
    //
    // Terminal programs do not hand their own environment to what runs inside them (gnome-terminal talks to a
    // shared server process, Terminal.app starts a login shell), so the API key cannot travel as environment
    // variables. The terminal runs this program instead, which reads the saved profile itself and starts Claude Code.
    // That also keeps the key out of command lines and script files.

    /// <summary>The terminal programs tried on Linux, in order, each with the argument that precedes the command to run.</summary>
    private static readonly (string Program, string[] Prefix)[] LinuxTerminals =
    [
        ("x-terminal-emulator", ["-e"]),
        ("gnome-terminal", ["--"]),
        ("konsole", ["-e"]),
        ("xfce4-terminal", ["-x"]),
        ("kitty", []),
        ("alacritty", ["-e"]),
        ("wezterm", ["start", "--"]),
        ("xterm", ["-e"]),
    ];

    /// <summary>A terminal program on PATH: <c>$TERMINAL</c> first, then the usual ones. Null when there is none.</summary>
    public static (string Program, string[] Prefix)? FindLinuxTerminal(string? pathVariable, string? terminalVariable)
    {
        bool OnPath(string program) => (pathVariable ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(dir => { try { return File.Exists(Path.Combine(dir, program)); } catch (ArgumentException) { return false; } });

        if (!string.IsNullOrWhiteSpace(terminalVariable) && OnPath(terminalVariable))
        {
            var known = LinuxTerminals.FirstOrDefault(t => t.Program == terminalVariable);
            return (terminalVariable, known.Prefix ?? ["-e"]);
        }
        foreach (var terminal in LinuxTerminals)
            if (OnPath(terminal.Program)) return terminal;
        return null;
    }

    /// <summary>The command a terminal window runs for an instance: this program again, in console-session mode.</summary>
    public static IReadOnlyList<string> SessionCommand(IReadOnlyList<string> self, InstanceInfo instance) =>
        [.. self, RunCliFlag, instance.Id];

    /// <summary>The process that opens a terminal window running <paramref name="command"/> (macOS and Linux).</summary>
    public static ProcessStartInfo BuildTerminalStartInfo(HostOs os, IReadOnlyList<string> command, string? pathVariable, string? terminalVariable)
    {
        if (os == HostOs.MacOS)
        {
            var script = HostPlatform.AppleScriptEscape(HostPlatform.ShellJoin(command));
            var mac = new ProcessStartInfo("osascript") { UseShellExecute = false };
            mac.ArgumentList.Add("-e");
            mac.ArgumentList.Add("tell application \"Terminal\" to activate");
            mac.ArgumentList.Add("-e");
            mac.ArgumentList.Add($"tell application \"Terminal\" to do script \"{script}\"");
            return mac;
        }

        var (program, prefix) = FindLinuxTerminal(pathVariable, terminalVariable)
            ?? throw new InvalidOperationException("No terminal program found. Install one (for example gnome-terminal, konsole or xterm) or set the TERMINAL variable.");
        var linux = new ProcessStartInfo(program) { UseShellExecute = false };
        foreach (var arg in prefix) linux.ArgumentList.Add(arg);
        foreach (var arg in command) linux.ArgumentList.Add(arg);
        return linux;
    }

    /// <summary>Process ids of the open console sessions of an instance (macOS and Linux; Windows tracks the windows it started).</summary>
    public static IReadOnlyList<int> OpenSessions(string instanceId, IReadOnlyList<UnixProcess>? processes = null)
    {
        var tail = $" {RunCliFlag} {instanceId}";
        return (processes ?? UnixProcesses.List()).Where(p => p.CommandLine.EndsWith(tail, StringComparison.Ordinal)).Select(p => p.Pid).ToList();
    }

    public static Process Start(ClaudeEnvironment env, InstanceInfo instance, ApiSettings settings)
    {
        var cli = FindCli(env) ?? throw new InvalidOperationException(HostPlatform.IsWindows
            ? "Claude Code (CLI) not found. To install it, run in PowerShell: irm https://claude.ai/install.ps1 | iex"
            : "Claude Code (CLI) not found. To install it, run in a terminal: curl -fsSL https://claude.ai/install.sh | bash");
        if (settings.ApiKey.Length == 0)
            throw new InvalidOperationException("Could not read the API key (it may have been saved by another user or computer). Enter the key again.");

        var info = HostPlatform.IsWindows
            ? BuildStartInfo(cli, instance, settings, env.Home)
            : BuildTerminalStartInfo(HostPlatform.Current, SessionCommand(HostPlatform.SelfCommand(), instance),
                Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("TERMINAL"));
        return Process.Start(info) ?? throw new InvalidOperationException("Could not start the console.");
    }
}
