using System.Diagnostics;
using System.Text;

namespace ClaudeMulti.Core;

public enum HostOs { Windows, MacOS, Linux }

/// <summary>Which operating system this is, plus the few places where the systems differ in small ways.</summary>
public static class HostPlatform
{
    public static HostOs Current { get; } =
        OperatingSystem.IsWindows() ? HostOs.Windows : OperatingSystem.IsMacOS() ? HostOs.MacOS : HostOs.Linux;

    public static bool IsWindows => Current == HostOs.Windows;

    /// <summary>Windows and the default macOS volume ignore case in file names; Linux does not.</summary>
    public static StringComparison PathComparison(HostOs os) =>
        os == HostOs.Linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static StringComparison PathComparison() => PathComparison(Current);

    /// <summary>Compares two folder paths the way the file system would, ignoring a trailing separator.</summary>
    public static bool SameFolder(string a, string b, HostOs os) =>
        string.Equals(TrimSeparators(Path.GetFullPath(a)), TrimSeparators(Path.GetFullPath(b)), PathComparison(os));

    public static bool SameFolder(string a, string b) => SameFolder(a, b, Current);

    private static string TrimSeparators(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        return trimmed.Length == 0 ? path : trimmed; // keep "/" itself
    }

    /// <summary>Shows a folder in the file manager (Explorer, Finder, or whatever handles folders on Linux).</summary>
    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        var program = Current switch { HostOs.Windows => "explorer.exe", HostOs.MacOS => "open", _ => "xdg-open" };
        var info = new ProcessStartInfo(program) { ArgumentList = { path }, UseShellExecute = false };
        if (Current == HostOs.Linux)
        {
            info.RedirectStandardOutput = info.RedirectStandardError = true; // xdg-open can be noisy
        }
        Process.Start(info)?.Dispose();
    }

    /// <summary>Quotes a value as one POSIX shell word.</summary>
    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>The command that starts this program again, as one program plus leading arguments (handles <c>dotnet app.dll</c>).</summary>
    public static IReadOnlyList<string> SelfCommand()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Application path not found.");
        var name = Path.GetFileNameWithoutExtension(exe);
        if (!string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase)) return [exe];
        var dll = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        return string.IsNullOrEmpty(dll) ? [exe] : [exe, dll];
    }

    /// <summary>Joins arguments into one POSIX shell command line.</summary>
    public static string ShellJoin(IEnumerable<string> args) => string.Join(' ', args.Select(ShellQuote));

    /// <summary>Escapes text for use inside an AppleScript string literal.</summary>
    public static string AppleScriptEscape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>Runs a small tool and returns its standard output, or null when it is missing or fails.</summary>
    public static string? RunTool(string program, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(program)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            using var process = Process.Start(info);
            if (process == null) return null;
            var errors = process.StandardError.ReadToEndAsync(); // drained so a chatty tool cannot block on a full pipe
            var output = process.StandardOutput.ReadToEnd();
            errors.Wait(2000);
            return process.WaitForExit(5000) && process.ExitCode == 0 ? output : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
