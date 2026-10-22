using System;
using ClaudeMulti.Core;

namespace ClaudeMulti.App.Infrastructure;

/// <summary>
/// Desktop notifications through what each system already has: a Windows toast (via PowerShell, which owns an
/// app identity Windows accepts), <c>osascript</c> on macOS and <c>notify-send</c> on Linux. A failure is silent:
/// a missed balloon must never disturb the app.
/// </summary>
public static class Notifier
{
    private const string WindowsScript =
        "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null;" +
        "$x = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02);" +
        "$t = $x.GetElementsByTagName('text');" +
        "$t.Item(0).AppendChild($x.CreateTextNode($env:CM_TITLE)) > $null;" +
        "$t.Item(1).AppendChild($x.CreateTextNode($env:CM_TEXT)) > $null;" +
        "$n = [Windows.UI.Notifications.ToastNotification]::new($x);" +
        "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe').Show($n)";

    public static void Show(string title, string text)
    {
        // Off the caller's thread: starting a process can take a second.
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                switch (HostPlatform.Current)
                {
                    case HostOs.Windows:
                        ShowWindows(title, text);
                        break;
                    case HostOs.MacOS:
                        HostPlatform.RunTool("osascript", "-e", "on run argv", "-e", "display notification (item 2 of argv) with title (item 1 of argv)", "-e", "end run", title, text);
                        break;
                    default:
                        HostPlatform.RunTool("notify-send", "--app-name=Claude Multi", title, text);
                        break;
                }
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException)
            {
                // No notification service here.
            }
        });
    }

    private static void ShowWindows(string title, string text)
    {
        var info = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", WindowsScript },
        };
        // The text travels in the environment, so it can never be read as part of the script.
        info.Environment["CM_TITLE"] = title;
        info.Environment["CM_TEXT"] = text;
        using var process = System.Diagnostics.Process.Start(info);
        process?.WaitForExit(10000);
    }
}
