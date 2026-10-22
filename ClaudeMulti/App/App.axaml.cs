using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using ClaudeMulti.App.Infrastructure;
using ClaudeMulti.App.ViewModels;
using ClaudeMulti.App.Views;
using ClaudeMulti.Core;

namespace ClaudeMulti.App;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? desktop;
    private SingleInstance? single;
    private MainWindow? window;
    private TrayIcon? tray;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            desktop = lifetime;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown; // the window hides to the tray; Exit quits
            Start(desktop.Args ?? []);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void Start(string[] args)
    {
        var env = ClaudeEnvironment.Detect();
        var store = new StateStore(env.StatePath);
        var log = new ManagerLog(env.LogPath);

        single = new SingleInstance(env.InstancesRoot);
        if (!single.TryBecomeFirst())
        {
            desktop!.Shutdown(); // the running manager was asked to come to the front
            return;
        }

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            log.Info("Unexpected error: " + e.Exception);
            e.Handled = true;
        };

        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Application path not found.");
        var router = new ProtocolRouter(ProtocolRegistries.Create(), store, exe);

        var trayAvailable = TrayAvailability.IsAvailable();
        window = new MainWindow { HideOnClose = trayAvailable };
        var model = new MainViewModel(env, store, log, window, router);
        window.DataContext = model;
        window.ExitRequested += Quit;
        desktop!.MainWindow = window;

        if (trayAvailable) CreateTray(model);
        model.Notify += Notifier.Show;
        model.Cache.ToastRaised += Notifier.Show;

        single.Listen(() => Dispatcher.UIThread.Post(ShowWindow));
        var pageArg = Array.IndexOf(args, "--page");
        if (pageArg >= 0 && pageArg + 1 < args.Length && int.TryParse(args[pageArg + 1], out var page)) window.GoTo(page);
        if (Array.IndexOf(args, "--minimized") < 0 || !trayAvailable) ShowWindow();
        _ = model.StartAsync();
    }

    private void CreateTray(MainViewModel model)
    {
        var menu = new NativeMenu();
        var open = new NativeMenuItem("Open");
        open.Click += (_, _) => ShowWindow();
        var sync = new NativeMenuItem("Sync now");
        sync.Click += (_, _) => _ = model.SyncAsync();
        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => Quit();
        menu.Items.Add(open);
        menu.Items.Add(sync);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);

        tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://ClaudeMulti/Assets/app.png"))),
            ToolTipText = "Claude Multi",
            Menu = menu,
        };
        tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, new TrayIcons { tray });
    }

    private void ShowWindow()
    {
        if (window == null) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void Quit()
    {
        if (tray != null) tray.IsVisible = false;
        window?.Quit();
        single?.Dispose();
        desktop?.Shutdown();
    }
}
