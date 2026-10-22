using System;
using Avalonia;
using ClaudeMulti.Core;
using ClaudeMulti.Core.Transfer;

namespace ClaudeMulti.App;

internal static class Program
{
    private const string UriFlag = "--uri";

    [STAThread]
    public static int Main(string[] args)
    {
        // Two modes that never open a window: the terminal session of an API instance (macOS, Linux)
        // and a claude:// link handed to this program by the system.
        if (args.Length >= 2 && args[0] == CliLauncher.RunCliFlag)
        {
            var env = ClaudeEnvironment.Detect();
            return CliSession.Run(env, args[1], KeyProtector.Create(env));
        }
        if (args.Length >= 2 && args[0] == UriFlag)
        {
            ForwardLink(args[1]);
            return 0;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    private static void ForwardLink(string uri)
    {
        var env = ClaudeEnvironment.Detect();
        var log = new ManagerLog(env.LogPath);
        log.Info("claude:// link received.");
        try
        {
            var target = InstanceLauncher.ForwardUri(env, new StateStore(env.StatePath), uri);
            log.Info(target == null ? "claude:// link could not be routed." : $"claude:// link sent to instance '{target.Id}'.");
        }
        catch (Exception e)
        {
            // Package activation reports failures as COMException; this process has no window, so log everything.
            log.Info("claude:// link error: " + e.Message);
        }
    }
}
