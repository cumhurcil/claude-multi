using System.Text;

namespace ClaudeMulti.Core;

/// <summary>One running process on macOS or Linux.</summary>
/// <param name="ExePath">The program's path when the system tells us (Linux), else null.</param>
/// <param name="CommandLine">Arguments joined into one line, with a spaced --user-data-dir value quoted like on Windows.</param>
public sealed record UnixProcess(int Pid, string? ExePath, string CommandLine);

/// <summary>Lists processes with their command lines: /proc on Linux, <c>ps</c> on macOS.</summary>
public static class UnixProcesses
{
    public static IReadOnlyList<UnixProcess> List() =>
        HostPlatform.Current == HostOs.Linux ? ListLinux() : ListWithPs();

    private static List<UnixProcess> ListLinux()
    {
        var result = new List<UnixProcess>();
        if (!Directory.Exists("/proc")) return ListWithPs();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try
            {
                var raw = File.ReadAllBytes(Path.Combine(dir, "cmdline"));
                if (raw.Length == 0) continue; // kernel threads
                var argv = FromNulSeparated(raw);
                string? exe = null;
                try { exe = new FileInfo(Path.Combine(dir, "exe")).LinkTarget; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                result.Add(new UnixProcess(pid, exe, FromArgv(argv)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The process ended, or belongs to another user.
            }
        }
        return result;
    }

    private static List<UnixProcess> ListWithPs()
    {
        var result = new List<UnixProcess>();
        var output = HostPlatform.RunTool("ps", "-axww", "-o", "pid=,args=");
        if (output == null) return result;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimStart();
            var space = trimmed.IndexOf(' ');
            if (space <= 0 || !int.TryParse(trimmed[..space], out var pid)) continue;
            result.Add(new UnixProcess(pid, null, FromPsArgs(trimmed[(space + 1)..].Trim())));
        }
        return result;
    }

    // ---- command line shapes (pure, so they can be tested anywhere) ----

    public static string[] FromNulSeparated(byte[] raw) =>
        Encoding.UTF8.GetString(raw).TrimEnd('\0').Split('\0');

    /// <summary>Joins an argv into one line; a <c>--user-data-dir</c> value with spaces is quoted so one parser reads every system.</summary>
    public static string FromArgv(IEnumerable<string> argv) =>
        string.Join(' ', argv.Select(a => a.StartsWith(UserDataDirFlag, StringComparison.Ordinal) && a.Any(char.IsWhiteSpace)
            ? $"{UserDataDirFlag}\"{a[UserDataDirFlag.Length..]}\""
            : a));

    private const string UserDataDirFlag = "--user-data-dir=";

    /// <summary>
    /// <c>ps</c> prints arguments separated by single spaces, so a path with spaces cannot be told apart from two
    /// arguments. A Chromium switch starts with "--", so the value is taken up to the next " --" (or the end).
    /// </summary>
    public static string FromPsArgs(string args)
    {
        var start = args.IndexOf(UserDataDirFlag, StringComparison.Ordinal);
        if (start < 0) return args;
        var valueStart = start + UserDataDirFlag.Length;
        if (valueStart < args.Length && args[valueStart] == '"') return args;
        var next = args.IndexOf(" --", valueStart, StringComparison.Ordinal);
        var end = next < 0 ? args.Length : next;
        var value = args[valueStart..end];
        return value.Any(char.IsWhiteSpace)
            ? args[..valueStart] + "\"" + value + "\"" + args[end..]
            : args;
    }
}
