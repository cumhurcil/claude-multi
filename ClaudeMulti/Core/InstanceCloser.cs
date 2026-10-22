using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace ClaudeMulti.Core;

/// <summary>Stops every process of one Claude Desktop instance: its windows, tray entry and helper processes.</summary>
public static partial class InstanceCloser
{
    [GeneratedRegex("""--user-data-dir=(?:"(?<q>[^"]*)"|(?<p>[^\s"]+))""", RegexOptions.IgnoreCase)]
    private static partial Regex UserDataDirArg();

    /// <summary>
    /// True when a command line is the root (browser) process of the given instance: not a Chromium child
    /// (<c>--type=</c>), and its <c>--user-data-dir</c> is the instance's folder (the main app has none).
    /// </summary>
    public static bool IsRootOf(string? commandLine, InstanceInfo instance) => IsRootOf(commandLine, instance, HostPlatform.Current);

    public static bool IsRootOf(string? commandLine, InstanceInfo instance, HostOs os)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || commandLine.Contains("--type=", StringComparison.OrdinalIgnoreCase))
            return false;

        var match = UserDataDirArg().Match(commandLine);
        if (!match.Success) return instance.IsMain;

        var dir = match.Groups["q"].Success ? match.Groups["q"].Value : match.Groups["p"].Value;
        return HostPlatform.SameFolder(dir, instance.DataDir, os);
    }

    /// <summary>True for the Claude Desktop executable (not the Claude Code CLI, which is also called claude.exe).</summary>
    public static bool IsDesktopExecutable(string? path, ClaudeEnvironment env)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (env.ClaudeExe != null)
            return string.Equals(Path.GetFullPath(path), Path.GetFullPath(env.ClaudeExe), HostPlatform.PathComparison());
        return path.Contains(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith(@"\app\Claude.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// macOS and Linux: whether a process runs the Claude Desktop program. On macOS argv[0] is the full path of the
    /// binary inside Claude.app; on Linux the kernel names the real executable, which is compared after resolving links.
    /// </summary>
    public static bool IsDesktopProcess(UnixProcess process, ClaudeEnvironment env, HostOs os)
    {
        var known = env.ClaudeExe;
        if (known == null) return false;
        var comparison = HostPlatform.PathComparison(os);

        var line = process.CommandLine;
        if (line.StartsWith(known, comparison) && (line.Length == known.Length || line[known.Length] == ' ')) return true;
        return process.ExePath != null
            && string.Equals(ResolveLinks(process.ExePath), ResolveLinks(known), comparison);
    }

    private static string ResolveLinks(string path)
    {
        try { return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return path; }
    }

    /// <summary>Kills the instance's process trees. Returns how many root processes were stopped.</summary>
    public static int Close(ClaudeEnvironment env, InstanceInfo instance) =>
        OperatingSystem.IsWindows() ? CloseWindows(env, instance) : CloseUnix(env, instance);

    [SupportedOSPlatform("windows")]
    private static int CloseWindows(ClaudeEnvironment env, InstanceInfo instance)
    {
        var closed = 0;
        foreach (var process in Process.GetProcessesByName("claude"))
        {
            using (process)
            {
                if (!IsRoot(process, env, instance)) continue;
                if (Kill(process)) closed++;
            }
        }
        return closed;
    }

    private static int CloseUnix(ClaudeEnvironment env, InstanceInfo instance)
    {
        var closed = 0;
        foreach (var candidate in UnixProcesses.List())
        {
            // An extra instance is named by its own --user-data-dir. The main app has none, so it must also be the Desktop program.
            if (!IsRootOf(candidate.CommandLine, instance)) continue;
            if (instance.IsMain && !IsDesktopProcess(candidate, env, HostPlatform.Current)) continue;
            try
            {
                using var process = Process.GetProcessById(candidate.Pid);
                if (Kill(process)) closed++;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // Already gone; the next status refresh shows what is left.
            }
        }
        return closed;
    }

    private static bool Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false; // already gone or protected
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsRoot(Process process, ClaudeEnvironment env, InstanceInfo instance)
    {
        try
        {
            return IsDesktopExecutable(process.MainModule?.FileName, env) && IsRootOf(WindowsCommandLine.Of(process), instance);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The command line of a 64-bit Windows process, or null when it cannot be read (always null elsewhere).</summary>
    public static string? CommandLineOf(Process process) =>
        OperatingSystem.IsWindows() ? WindowsCommandLine.Of(process) : null;

    // ---- reading another process's command line (x64: PEB -> ProcessParameters -> CommandLine) ----

    [SupportedOSPlatform("windows")]
    private static class WindowsCommandLine
    {
        private const int ProcessBasicInformationClass = 0;
        private const int PebProcessParametersOffset = 0x20;
        private const int ParametersCommandLineOffset = 0x70;

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessBasicInformation
        {
            public IntPtr ExitStatus;
            public IntPtr PebBaseAddress;
            public IntPtr AffinityMask;
            public IntPtr BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInformation info, int size, out int returned);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);

        public static string? Of(Process process)
        {
            if (!Environment.Is64BitProcess) return null;
            var handle = process.Handle;
            var info = new ProcessBasicInformation();
            if (NtQueryInformationProcess(handle, ProcessBasicInformationClass, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0)
                return null;

            var parameters = ReadPointer(handle, info.PebBaseAddress + PebProcessParametersOffset);
            if (parameters == IntPtr.Zero) return null;

            // UNICODE_STRING: Length (2) + MaximumLength (2) + padding (4) + Buffer (8)
            var header = ReadBytes(handle, parameters + ParametersCommandLineOffset, 16);
            if (header == null) return null;
            var length = BitConverter.ToUInt16(header, 0);
            var buffer = (IntPtr)BitConverter.ToInt64(header, 8);
            if (length == 0 || buffer == IntPtr.Zero) return null;

            var text = ReadBytes(handle, buffer, length);
            return text == null ? null : System.Text.Encoding.Unicode.GetString(text);
        }

        private static IntPtr ReadPointer(IntPtr process, IntPtr address)
        {
            var bytes = ReadBytes(process, address, IntPtr.Size);
            return bytes == null ? IntPtr.Zero : (IntPtr)BitConverter.ToInt64(bytes, 0);
        }

        private static byte[]? ReadBytes(IntPtr process, IntPtr address, int count)
        {
            var buffer = new byte[count];
            return ReadProcessMemory(process, address, buffer, (IntPtr)count, out var read) && read == count ? buffer : null;
        }
    }
}
