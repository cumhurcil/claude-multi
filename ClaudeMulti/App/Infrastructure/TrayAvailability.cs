using ClaudeMulti.Core;

namespace ClaudeMulti.App.Infrastructure;

public static class TrayAvailability
{
    /// <summary>
    /// Windows and macOS always have a notification area / menu bar. On Linux the icon needs a StatusNotifier host
    /// (KDE, or GNOME with the AppIndicator extension); without one a hidden window could never be brought back,
    /// so the window then closes the program instead of hiding.
    /// </summary>
    public static bool IsAvailable()
    {
        if (HostPlatform.Current != HostOs.Linux) return true;
        var reply = HostPlatform.RunTool("dbus-send", "--session", "--print-reply", "--dest=org.freedesktop.DBus",
            "/org/freedesktop/DBus", "org.freedesktop.DBus.NameHasOwner", "string:org.kde.StatusNotifierWatcher");
        return reply != null && reply.Contains("boolean true");
    }
}
