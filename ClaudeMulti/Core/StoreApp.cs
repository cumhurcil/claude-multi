using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace ClaudeMulti.Core;

/// <summary>
/// Claude installed as an MSIX package (the Store build, and also what claude.ai's own installer sets up on
/// current Windows builds). Its Claude.exe lives under WindowsApps and refuses to be started directly
/// ("Access denied"); it has to be started through package activation, which still passes arguments on.
/// </summary>
public static class StoreApp
{
    private const string PackageName = "Claude";
    private const string RepositoryKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    /// <summary>The AppUserModelId ("Claude_&lt;publisher&gt;!Claude") of the newest installed Claude package, or null.</summary>
    public static string? FindAppId()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(RepositoryKey);
            if (packages == null) return null;

            string? bestId = null;
            Version? bestVersion = null;
            foreach (var fullName in packages.GetSubKeyNames())
            {
                var version = VersionOf(fullName);
                var appId = AppIdOf(fullName);
                if (version == null || appId == null) continue;
                if (bestVersion != null && version <= bestVersion) continue;

                using var key = packages.OpenSubKey(fullName);
                var root = key?.GetValue("PackageRootFolder") as string;
                if (root == null || !File.Exists(Path.Combine(root, "app", "Claude.exe"))) continue;

                bestId = appId;
                bestVersion = version;
            }
            return bestId;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>"Claude_2.42304.0.0_x64__pzs8sxrjxfjjc" becomes "Claude_pzs8sxrjxfjjc!Claude"; other packages give null.</summary>
    public static string? AppIdOf(string packageFullName)
    {
        var parts = packageFullName.Split('_');
        if (parts.Length < 5 || parts[0] != PackageName) return null;
        var publisherId = parts[^1];
        return publisherId.Length == 0 ? null : $"{PackageName}_{publisherId}!{PackageName}";
    }

    private static Version? VersionOf(string packageFullName)
    {
        var parts = packageFullName.Split('_');
        return parts.Length >= 5 && parts[0] == PackageName && Version.TryParse(parts[1], out var v) ? v : null;
    }

    /// <summary>Joins arguments into one Windows command line, quoting the ones that need it.</summary>
    public static string JoinArguments(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    private static string Quote(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c == '"')) return arg;

        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
            else sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        return sb.Append('\\', backslashes * 2).Append('"').ToString();
    }

    /// <summary>Starts the package's app with the given arguments (empty means a plain launch).</summary>
    [SupportedOSPlatform("windows")]
    public static void Launch(string appId, IReadOnlyCollection<string> args)
    {
        var manager = (IApplicationActivationManager)new ApplicationActivationManager();
        var commandLine = args.Count == 0 ? null : JoinArguments(args);
        var hr = manager.ActivateApplication(appId, commandLine, 0, out _);
        Marshal.ThrowExceptionForHR(hr);
    }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);

        [PreserveSig]
        int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray,
            [MarshalAs(UnmanagedType.LPWStr)] string? verb, out uint processId);

        [PreserveSig]
        int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray, out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager { }
}
