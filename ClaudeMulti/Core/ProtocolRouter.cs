using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace ClaudeMulti.Core;

public interface IProtocolRegistry
{
    string? GetCommand();
    void SetCommand(string command);

    /// <summary>
    /// The ProgId Windows really opens claude: links with, or null when unknown. A Store (MSIX) Claude declares the
    /// protocol in its package, so Windows ignores the plain claude\shell\open\command key and starts the package.
    /// </summary>
    string? ResolveProgId() => null;

    /// <summary>Makes the manager pickable as the claude: handler in Windows' default-app settings.</summary>
    void RegisterHandler(string command) { }

    /// <summary>Removes the manager as the handler when there was none before (Linux; Windows leaves the key as it is).</summary>
    void ClearCommand() { }

    /// <summary>False where the system gives programs no way to take over claude: links (macOS).</summary>
    bool IsSupported => true;

    string? UnsupportedReason => null;
}

/// <summary>The current user's claude:// handler (HKCU, no admin rights needed).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProtocolRegistry : IProtocolRegistry
{
    private const string KeyPath = @"Software\Classes\claude\shell\open\command";
    private const string CapabilitiesPath = @"Software\ClaudeMulti\Capabilities";
    private const uint AssocfIsProtocol = 0x1000;
    private const int AssocstrProgId = 20;

    public string? GetCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue("") as string;
    }

    public void SetCommand(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue("", command, RegistryValueKind.String);
    }

    public string? ResolveProgId()
    {
        var buffer = new StringBuilder(512);
        var length = (uint)buffer.Capacity;
        return AssocQueryStringW(AssocfIsProtocol, AssocstrProgId, "claude", "open", buffer, ref length) == 0 ? buffer.ToString() : null;
    }

    public void RegisterHandler(string command)
    {
        using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProtocolRouter.HandlerProgId}"))
        {
            progId.SetValue("", "URL:Claude Multi");
            progId.SetValue("URL Protocol", "");
        }
        using (var open = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProtocolRouter.HandlerProgId}\shell\open\command"))
            open.SetValue("", command, RegistryValueKind.String);
        using (var capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesPath))
        {
            capabilities.SetValue("ApplicationName", "Claude Multi");
            capabilities.SetValue("ApplicationDescription", "Routes Claude sign-in links to the right instance.");
        }
        using (var urls = Registry.CurrentUser.CreateSubKey(CapabilitiesPath + @"\UrlAssociations"))
            urls.SetValue("claude", ProtocolRouter.HandlerProgId);
        using (var apps = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            apps.SetValue("ClaudeMulti", CapabilitiesPath);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryStringW(uint flags, int str, string assoc, string? extra, StringBuilder output, ref uint length);
}

/// <summary>
/// Routes browser sign-in links (claude://...) to the instance the user is signing in to,
/// by making this manager the claude:// handler and forwarding each link.
/// </summary>
public sealed class ProtocolRouter(IProtocolRegistry registry, StateStore store, string managerExe)
{
    public static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(20);
    public const string HandlerProgId = "ClaudeMulti.Url";
    private const string UriMarker = "--uri \"%1\"";

    public string HookCommand => $"\"{managerExe}\" {UriMarker}";

    public bool IsSupported => registry.IsSupported;

    public string? UnsupportedReason => registry.UnsupportedReason;

    public bool IsHooked => IsSame(registry.GetCommand(), HookCommand);

    /// <summary>
    /// True when Windows will really hand claude: links to this manager: either the user picked it as the default
    /// handler, or the plain registry key is ours and nothing (like an MSIX package) overrides it.
    /// </summary>
    public bool IsRoutedHere
    {
        get
        {
            var progId = registry.ResolveProgId();
            if (progId == null) return IsHooked;
            return string.Equals(progId, HandlerProgId, StringComparison.OrdinalIgnoreCase)
                || (string.Equals(progId, "claude", StringComparison.OrdinalIgnoreCase) && IsHooked);
        }
    }

    /// <summary>Keeps the manager listed as a claude: handler (also after the exe moved).</summary>
    public void RegisterHandler() => registry.RegisterHandler(HookCommand);

    public void Enable()
    {
        if (!registry.IsSupported) throw new IOException(registry.UnsupportedReason);
        var current = registry.GetCommand();
        store.Update(s =>
        {
            s.HookEnabled = true;
            if (current != null && !IsOurs(current)) s.OriginalProtocolCommand = current;
        });
        registry.SetCommand(HookCommand);
        RegisterHandler();
    }

    /// <summary>The ClaudeMulti.Url registration stays on purpose: if the user picked it as default, links keep reaching the manager, which sends them to the main app while no sign-in is pending.</summary>
    public void Disable()
    {
        var original = store.Load().OriginalProtocolCommand;
        if (original != null) registry.SetCommand(original);
        else registry.ClearCommand();
        store.Update(s =>
        {
            s.HookEnabled = false;
            s.OriginalProtocolCommand = null;
        });
    }

    /// <summary>Claude re-registers itself when it starts; put the hook back if it did. Returns true when it did.</summary>
    public bool Reassert()
    {
        if (!store.Load().HookEnabled) return false;
        var current = registry.GetCommand();
        if (IsSame(current, HookCommand)) return false;
        store.Update(s =>
        {
            if (current != null && !IsOurs(current)) s.OriginalProtocolCommand = current;
        });
        registry.SetCommand(HookCommand);
        return true;
    }

    /// <summary>The instance a link should go to: the one signing in (while pending), else the main app.</summary>
    public static InstanceInfo? ResolveTarget(ManagerState state, IReadOnlyList<InstanceInfo> instances, DateTimeOffset now)
    {
        if (state.PendingLoginId != null && state.PendingSince is { } since && now - since < PendingTtl)
        {
            var pending = instances.FirstOrDefault(i => i.Id == state.PendingLoginId);
            if (pending is { IsApi: false }) return pending;
        }
        // API instances never take a claude.ai sign-in link.
        return instances.FirstOrDefault(i => i.IsMain) ?? instances.FirstOrDefault(i => !i.IsApi);
    }

    public static bool IsClaudeUri(string? text) =>
        text is { Length: > 9 and <= 8192 }
        && text.StartsWith("claude://", StringComparison.OrdinalIgnoreCase)
        && !text.Any(char.IsControl);

    private static bool IsOurs(string command) => command.Contains(UriMarker, StringComparison.Ordinal);

    private static bool IsSame(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
