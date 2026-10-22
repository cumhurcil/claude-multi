using System.Text;

namespace ClaudeMulti.Core;

public static class ProtocolRegistries
{
    public static IProtocolRegistry Create() => HostPlatform.Current switch
    {
        HostOs.Windows when OperatingSystem.IsWindows() => new WindowsProtocolRegistry(),
        HostOs.Linux => new LinuxProtocolRegistry(),
        _ => new UnsupportedProtocolRegistry(
            "macOS only sends a claude:// link to an app bundle that declares it, and this manager is not packaged as one yet. " +
            "Sign-in links go to whichever Claude macOS picks; sign in to an extra instance through Claude's own sign-in page."),
    };
}

/// <summary>A system where the manager cannot take over claude:// links. Reads as "nothing registered" and refuses changes.</summary>
public sealed class UnsupportedProtocolRegistry(string reason) : IProtocolRegistry
{
    public bool IsSupported => false;
    public string? UnsupportedReason => reason;
    public string? GetCommand() => null;
    public void SetCommand(string command) => throw new IOException(reason);
}

/// <summary>
/// The claude: handler on Linux desktops: the default application for the <c>x-scheme-handler/claude</c> type
/// (freedesktop.org), kept in the user's mimeapps.list and set with <c>xdg-mime</c>. The "command" the router
/// stores is the manager's own command line, or <c>desktop:&lt;file&gt;</c> for any other handler so it can be restored.
/// </summary>
public sealed class LinuxProtocolRegistry : IProtocolRegistry
{
    public const string MimeType = "x-scheme-handler/claude";
    public const string DesktopFileName = "claude-multi-handler.desktop";
    private const string OtherHandlerPrefix = "desktop:";

    private readonly string dataDir;
    private readonly string configDir;

    public LinuxProtocolRegistry()
        : this(Environment.GetEnvironmentVariable("XDG_DATA_HOME"), Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) { }

    public LinuxProtocolRegistry(string? xdgDataHome, string? xdgConfigHome, string home)
    {
        dataDir = string.IsNullOrWhiteSpace(xdgDataHome) ? Path.Combine(home, ".local", "share") : xdgDataHome;
        configDir = string.IsNullOrWhiteSpace(xdgConfigHome) ? Path.Combine(home, ".config") : xdgConfigHome;
    }

    private string DesktopFilePath => Path.Combine(dataDir, "applications", DesktopFileName);
    private string MimeAppsPath => Path.Combine(configDir, "mimeapps.list");

    public string? GetCommand()
    {
        var current = DefaultHandler();
        if (string.IsNullOrEmpty(current)) return null;
        if (!string.Equals(current, DesktopFileName, StringComparison.Ordinal)) return OtherHandlerPrefix + current;
        return CommandFromExec(ReadExec(DesktopFilePath)) ?? OtherHandlerPrefix + current;
    }

    public void SetCommand(string command)
    {
        if (command.StartsWith(OtherHandlerPrefix, StringComparison.Ordinal))
        {
            MakeDefault(command[OtherHandlerPrefix.Length..]);
            return;
        }
        WriteDesktopFile(command);
        MakeDefault(DesktopFileName);
    }

    public void RegisterHandler(string command) => WriteDesktopFile(command);

    public void ClearCommand()
    {
        if (!File.Exists(MimeAppsPath)) return;
        var text = File.ReadAllText(MimeAppsPath);
        var cleaned = RemoveDefault(text, MimeType, DesktopFileName);
        if (cleaned != text) File.WriteAllText(MimeAppsPath, cleaned);
    }

    private string? DefaultHandler()
    {
        if (File.Exists(MimeAppsPath) && ParseDefault(File.ReadAllText(MimeAppsPath), MimeType) is { } fromFile) return fromFile;
        return HostPlatform.RunTool("xdg-mime", "query", "default", MimeType)?.Trim();
    }

    private static void MakeDefault(string desktopId)
    {
        if (HostPlatform.RunTool("xdg-mime", "default", desktopId, MimeType) == null)
            throw new IOException("Could not set the default app for claude: links (is xdg-mime installed?).");
    }

    private void WriteDesktopFile(string command)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DesktopFilePath)!);
        File.WriteAllText(DesktopFilePath, BuildDesktopEntry(command), new UTF8Encoding(false));
        HostPlatform.RunTool("update-desktop-database", Path.GetDirectoryName(DesktopFilePath)!); // optional
    }

    private static string? ReadExec(string desktopFile)
    {
        try
        {
            return File.ReadLines(desktopFile).FirstOrDefault(l => l.StartsWith("Exec=", StringComparison.Ordinal))?[5..];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- file formats (pure, so they can be tested anywhere) ----

    /// <summary>The first default application listed for a MIME type in a mimeapps.list text, or null.</summary>
    public static string? ParseDefault(string mimeApps, string mimeType)
    {
        var inDefaults = false;
        foreach (var raw in mimeApps.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inDefaults = line == "[Default Applications]"; continue; }
            if (!inDefaults || !line.StartsWith(mimeType + "=", StringComparison.Ordinal)) continue;
            var first = line[(mimeType.Length + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (first != null) return first;
        }
        return null;
    }

    /// <summary>The text without the default entry for a MIME type, but only when it points at <paramref name="desktopId"/>.</summary>
    public static string RemoveDefault(string mimeApps, string mimeType, string desktopId)
    {
        var inDefaults = false;
        var kept = new List<string>();
        foreach (var raw in mimeApps.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) inDefaults = line == "[Default Applications]";
            else if (inDefaults && line.StartsWith(mimeType + "=", StringComparison.Ordinal)
                     && line[(mimeType.Length + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() == desktopId)
                continue;
            kept.Add(raw);
        }
        return string.Join('\n', kept);
    }

    /// <summary>A .desktop file whose Exec is the router's command, with the Windows "%1" placeholder turned into "%u".</summary>
    public static string BuildDesktopEntry(string command) =>
        "[Desktop Entry]\n" +
        "Type=Application\n" +
        "Name=Claude Multi\n" +
        "Comment=Routes Claude sign-in links to the right instance\n" +
        $"Exec={command.Replace("\"%1\"", "%u")}\n" +
        "Terminal=false\n" +
        "NoDisplay=true\n" +
        $"MimeType={MimeType};\n";

    /// <summary>The router's command line back from a .desktop Exec value ("%u" becomes "\"%1\""), or null when there is none.</summary>
    public static string? CommandFromExec(string? exec) => string.IsNullOrEmpty(exec) ? null : exec.Replace("%u", "\"%1\"");
}
