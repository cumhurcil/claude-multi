using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using ClaudeMulti.App.Infrastructure;
using ClaudeMulti.Core;
using ClaudeMulti.Core.Transfer;

namespace ClaudeMulti.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 300;

    private readonly ClaudeEnvironment env;
    private readonly StateStore store;
    private readonly ManagerLog log;
    private readonly SyncEngine engine;
    private readonly ProtocolRouter router;
    private readonly IDialogs dialogs;
    private readonly IKeyProtector keyProtector;
    private readonly ConcurrentDictionary<string, List<Process>> cliWindows = new(); // Windows: the PowerShell windows this app opened
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer syncTimer = new();
    private readonly DispatcherTimer hookTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    private bool syncing;
    private bool refreshing;
    private bool hookEnabled;
    private string lastSyncText = "Not synced yet";
    private string lastSyncShort = "Not synced yet";
    private string hookStatus = "";

    public MainViewModel(ClaudeEnvironment env, StateStore store, ManagerLog log, IDialogs dialogs, ProtocolRouter router)
    {
        this.env = env;
        this.store = store;
        this.log = log;
        this.dialogs = dialogs;
        this.router = router;
        keyProtector = KeyProtector.Create(env);
        engine = new SyncEngine(env, store);

        var state = store.Load();
        syncChats = state.SyncChats;
        syncSettings = state.SyncSettings;
        autoSync = state.AutoSync;
        hookEnabled = state.HookEnabled && router.IsSupported;
        Interval = new ChoiceGroup(state.IntervalSeconds.ToString(), ("30 s", "30"), ("1 min", "60"), ("2 min", "120"), ("5 min", "300"));
        Interval.Changed += () => { SaveState(s => s.IntervalSeconds = int.Parse(Interval.Value)); ApplyTimer(); };

        AddCommand = new RelayCommand(AddInstance);
        AddApiCommand = new RelayCommand(AddApiInstance);
        SyncNowCommand = new RelayCommand(() => _ = SyncAsync(), () => !syncing).Tracked();
        OpenLogFolderCommand = new RelayCommand(() => OpenFolder(env.InstancesRoot));

        Cache = new CacheViewModel(store, env.BackupsDir, log.Info);
        Transfer = new TransferViewModel(env, store, log, dialogs, keyProtector, () => _ = RefreshAsync());

        log.Written += line => Dispatcher.UIThread.Post(() =>
        {
            Log.Insert(0, line);
            while (Log.Count > MaxLogLines) Log.RemoveAt(Log.Count - 1);
        });

        refreshTimer.Tick += async (_, _) => await RefreshAsync();
        syncTimer.Tick += async (_, _) => { if (this.autoSync) await SyncAsync(); };
        hookTimer.Tick += (_, _) => WatchHook();
    }

    public ObservableCollection<InstanceViewModel> Instances { get; } = [];
    public ObservableCollection<string> Log { get; } = [];
    public ChoiceGroup Interval { get; }
    public CacheViewModel Cache { get; }
    public TransferViewModel Transfer { get; }

    public ICommand AddCommand { get; }
    public ICommand AddApiCommand { get; }
    public ICommand SyncNowCommand { get; }
    public ICommand OpenLogFolderCommand { get; }

    public event Action<string, string>? Notify;

    // ---- what differs between systems, for the pages' texts ----

    /// <summary>A note shown on the Instances page when Claude Desktop is missing or unofficial on this system; empty otherwise.</summary>
    public string PlatformNotice => HostPlatform.Current switch
    {
        HostOs.Linux when env.CanLaunch =>
            $"Anthropic ships no Claude Desktop for Linux. Using the community build found at {env.ClaudeExe}.",
        HostOs.Linux =>
            "Anthropic ships no Claude Desktop for Linux, so Desktop instances need a community build: put its launcher on PATH as 'claude-desktop' " +
            $"or set {ClaudeEnvironment.ExeOverrideVariable} to its executable. Instances with an API key (Claude Code in a terminal), the prompt cache timer and Claude Code sync work without it.",
        _ when !env.CanLaunch => "Claude Desktop was not found. Install it, or set " + ClaudeEnvironment.ExeOverrideVariable + " to its executable.",
        _ => "",
    };

    public bool HasPlatformNotice => PlatformNotice.Length > 0;

    public bool RoutingSupported => router.IsSupported;
    public bool RoutingUnsupported => !router.IsSupported;
    public string RoutingUnsupportedText => router.UnsupportedReason ?? "";

    public string RoutingHelp => HostPlatform.Current switch
    {
        HostOs.Windows =>
            "1. On the Instances page, press 'Sign in' on the instance you want to sign in to.\n2. The instance opens; sign in with your account in the browser and confirm the 'Open Claude' prompt.\n3. The link goes to that instance automatically. With no sign-in pending it goes to the main Claude.\n\n" +
            "If Claude is the Store (package) version, Windows sends the link straight to Claude: on the first sign-in, in the Settings page that opens, choose 'Choose default apps by protocol' → CLAUDE → 'Claude Multi' (once).\n\n" +
            "This feature points the claude:// registration in Windows (for your user only) at this manager; turning it off restores the old registration. Turn it off before deleting the manager.",
        HostOs.Linux =>
            "1. On the Instances page, press 'Sign in' on the instance you want to sign in to.\n2. The instance opens; sign in with your account in the browser and confirm the 'Open Claude' prompt.\n3. The link goes to that instance automatically. With no sign-in pending it goes to the main Claude.\n\n" +
            "This feature makes this manager the default application for claude:// links (a .desktop file in ~/.local/share/applications and your mimeapps.list); turning it off restores the previous default. Turn it off before deleting the manager.",
        _ => "Sign-in routing is not available on macOS yet: macOS only hands claude:// links to an app bundle that declares them, and this manager is not packaged as one.",
    };

    public string SyncBackupsText => $"Settings files are backed up before they change ({Path.Combine(env.InstancesRoot, "backups")}, last 20).";

    // ---- start ----

    public async Task StartAsync()
    {
        log.Info("Started.");
        if (!env.CanLaunch)
            log.Info("Warning: Claude Desktop not found; opening instances will not work.");
        else if (env.ClaudeExe == null)
            log.Info($"Claude Desktop found as a package ({env.StoreAppId}); instances open through package activation.");
        if (hookEnabled)
        {
            try { router.RegisterHandler(); }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                log.Info("Could not write the default-app registration: " + e.Message);
            }
            WatchHook();
        }
        await RefreshAsync();
        ApplyTimer();
        refreshTimer.Start();
        hookTimer.Start();
        _ = SyncAsync();
    }

    private void ApplyTimer()
    {
        syncTimer.Stop();
        syncTimer.Interval = TimeSpan.FromSeconds(int.Parse(Interval.Value));
        if (autoSync) syncTimer.Start();
    }

    // ---- options ----

    private bool syncChats, syncSettings, autoSync;

    public bool SyncChats { get => syncChats; set { if (Set(ref syncChats, value)) SaveState(s => s.SyncChats = value); } }
    public bool SyncSettings { get => syncSettings; set { if (Set(ref syncSettings, value)) SaveState(s => s.SyncSettings = value); } }
    public bool AutoSync { get => autoSync; set { if (Set(ref autoSync, value)) { SaveState(s => s.AutoSync = value); ApplyTimer(); } } }

    public string LastSyncText { get => lastSyncText; private set => Set(ref lastSyncText, value); }
    public string LastSyncShort { get => lastSyncShort; private set => Set(ref lastSyncShort, value); }
    public bool IsSyncing { get => syncing; private set { if (Set(ref syncing, value)) RelayCommand.Requery(); } }

    private void SaveState(Action<ManagerState> change) => store.Update(change);

    // ---- sign-in routing ----

    public bool HookEnabled
    {
        get => hookEnabled;
        set
        {
            if (value == hookEnabled) return;
            try
            {
                if (value) router.Enable(); else router.Disable();
                hookEnabled = value;
                log.Info(value ? "Sign-in routing turned on." : "Sign-in routing turned off, previous setting restored.");
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                _ = dialogs.Alert("Sign-in routing", "Could not change the claude:// registration: " + e.Message);
            }
            Raise();
            UpdateHookStatus();
        }
    }

    public string HookStatus { get => hookStatus; private set => Set(ref hookStatus, value); }

    private void WatchHook()
    {
        try
        {
            if (hookEnabled && router.Reassert()) log.Info("Claude had refreshed its own link registration; routing was put back.");
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            log.Info("Routing check failed: " + e.Message);
        }
        UpdateHookStatus();
    }

    /// <summary>
    /// Store (MSIX) Claude owns claude: links through its package, so the registry key alone is ignored.
    /// Windows only lets the user pick another default handler, so open that page and say what to choose.
    /// </summary>
    private async Task AskForDefaultHandler()
    {
        if (!HostPlatform.IsWindows || router.IsRoutedHere) return;
        if (!await dialogs.Confirm("Windows setting required",
                "Windows sends the browser's claude:// link straight to the main Claude, so it never reaches the instance you chose.\n\n" +
                "Only you can change this, and only once: pressing Yes opens Settings. There choose 'Choose default apps by protocol' → the 'CLAUDE' row → 'Claude Multi'.\n\n" +
                "Open Settings now?"))
            return;
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception e)
        {
            await dialogs.Alert("Could not open Settings", e.Message);
        }
    }

    private void UpdateHookStatus()
    {
        if (!router.IsSupported) { HookStatus = "Not available on this system."; return; }
        if (!hookEnabled) { HookStatus = "Off: browser sign-in always returns to the main Claude."; return; }
        if (!router.IsRoutedHere)
        {
            HookStatus = "On, but Windows still sends the sign-in link to Claude: in Settings choose 'Claude Multi' for claude.";
            return;
        }
        var state = store.Load();
        var target = ProtocolRouter.ResolveTarget(state, InstanceCatalog.List(env), DateTimeOffset.UtcNow);
        var pending = state.PendingLoginId != null && state.PendingSince is { } since && DateTimeOffset.UtcNow - since < ProtocolRouter.PendingTtl;
        HookStatus = target == null ? "On." : pending
            ? $"On: the next sign-in link will go to '{LabelOf(target.Id)}'."
            : $"On: no sign-in pending, links go to '{LabelOf(target.Id)}'.";
    }

    // ---- instances ----

    public string LabelOf(string id) =>
        store.Load().Labels.TryGetValue(id, out var l) && !string.IsNullOrWhiteSpace(l) ? l : id == ClaudeEnvironment.MainId ? "Main Claude" : $"Instance {id}";

    private async Task RefreshAsync()
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            var snapshot = await Task.Run(() =>
            {
                var all = InstanceCatalog.List(env);
                var processes = !HostPlatform.IsWindows && all.Any(i => i.IsApi) ? UnixProcesses.List() : null;
                return all
                    .Select(i => (Info: i, Running: i.IsApi ? IsCliOpen(i, processes) : InstanceProbe.IsRunning(i), Signed: InstanceProbe.IsSignedIn(i),
                                  Account: InstanceProbe.SignedInAccount(i), Chats: InstanceProbe.ChatCount(i), Api: ApiSummaryOf(i)))
                    .ToList();
            });

            foreach (var stale in Instances.Where(v => snapshot.All(s => s.Info.Id != v.Id)).ToList()) Instances.Remove(stale);
            foreach (var item in snapshot)
            {
                var vm = Instances.FirstOrDefault(v => v.Id == item.Info.Id);
                if (vm == null)
                {
                    vm = new InstanceViewModel(item.Info,
                        new RelayCommand(() => Open(item.Info)), new RelayCommand(() => SignIn(item.Info)), new RelayCommand(() => _ = EditApi(item.Info)),
                        new RelayCommand(() => OpenFolder(item.Info.DataDir)), new RelayCommand(() => Rename(item.Info)),
                        new RelayCommand(() => Remove(item.Info)), new RelayCommand(() => CloseInstance(item.Info)));
                    Instances.Add(vm);
                }
                vm.Update(LabelOf(item.Info.Id), item.Running, item.Signed, item.Account, item.Chats, item.Api);
            }
            UpdateHookStatus();
        }
        finally
        {
            refreshing = false;
        }
    }

    private async void Open(InstanceInfo instance)
    {
        if (instance.IsApi) { OpenCli(instance); return; }
        try
        {
            if (!InstanceProbe.IsRunning(instance)) await Task.Run(engine.Run); // bring settings up to date before it starts
            if (!instance.IsApi && !InstanceProbe.IsSignedIn(instance)) InstanceLauncher.MarkLoginPending(store, instance);
            InstanceLauncher.Start(env, instance);
            log.Info($"'{LabelOf(instance.Id)}' opened.");
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or IOException)
        {
            await dialogs.Alert("Could not open", e.Message);
        }
    }

    private async void SignIn(InstanceInfo instance)
    {
        if (instance.IsApi) { _ = EditApi(instance); return; }
        try
        {
            if (router.IsSupported)
            {
                if (!hookEnabled && await dialogs.Confirm("Sign-in routing",
                        "For the browser's sign-in link to reach this instance, 'Sign-in routing' must be on. Turn it on now?\n\n(The claude:// link registration is temporarily pointed at this manager; you can turn it off on the Sign-in routing page.)"))
                    HookEnabled = true;
                if (hookEnabled) await AskForDefaultHandler();
            }

            if (!InstanceProbe.IsRunning(instance)) await Task.Run(engine.Run);
            InstanceLauncher.MarkLoginPending(store, instance);
            InstanceLauncher.Start(env, instance);
            log.Info($"Waiting for sign-in on '{LabelOf(instance.Id)}'.");
            UpdateHookStatus();
            if (router.IsSupported)
                Notify?.Invoke("Waiting for sign-in", $"Sign in in the browser; the link will go to '{LabelOf(instance.Id)}'.");
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or IOException)
        {
            await dialogs.Alert("Could not open", e.Message);
        }
    }

    private async void AddInstance()
    {
        var next = Enumerable.Range(2, 98).Select(n => n.ToString()).FirstOrDefault(n => !Directory.Exists(env.DataDirOf(n))) ?? "new";
        var name = await dialogs.Prompt("New instance", "Instance name (used as the folder name)", next, n => InstanceCatalog.ValidateName(n.Trim())
            ?? (Directory.Exists(env.DataDirOf(n.Trim())) ? "This name is already in use." : null));
        if (name == null) return;
        try
        {
            var created = InstanceCatalog.Create(env, name.Trim());
            log.Info($"New instance created: {created.Id}");
            _ = SyncAsync();
            _ = RefreshAsync();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException)
        {
            await dialogs.Alert("Could not create", e.Message);
        }
    }

    // ---- API key sign-in ----

    /// <summary>Opens a new console with Claude Code on the instance's API key. Every click opens another window.</summary>
    private async void OpenCli(InstanceInfo instance)
    {
        try
        {
            var settings = ApiProfile.Load(instance, keyProtector) ?? throw new InvalidOperationException("Could not read the API settings.");
            var process = CliLauncher.Start(env, instance, settings);
            if (HostPlatform.IsWindows)
                cliWindows.AddOrUpdate(instance.Id, _ => [process], (_, list) => [.. list.Where(p => !HasExited(p)), process]);
            else
                process.Dispose(); // the terminal program only hands the window over; the session is found by its command line
            log.Info($"'{LabelOf(instance.Id)}' opened as Claude Code (CLI) · model {settings.Models[0]}.");
            _ = RefreshAsync();
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            await dialogs.Alert("Could not open", e.Message);
        }
    }

    private bool IsCliOpen(InstanceInfo instance, IReadOnlyList<UnixProcess>? processes = null) =>
        HostPlatform.IsWindows
            ? cliWindows.TryGetValue(instance.Id, out var list) && list.Any(p => !HasExited(p))
            : CliLauncher.OpenSessions(instance.Id, processes).Count > 0;

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception) { return true; }
    }

    private string? ApiSummaryOf(InstanceInfo instance)
    {
        if (!instance.IsApi) return null;
        var settings = ApiProfile.Load(instance, keyProtector);
        if (settings == null) return "settings unreadable";
        var host = Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var uri) ? uri.Host : settings.BaseUrl;
        var key = settings.ApiKey.Length > 0 ? ApiProfile.Mask(settings.ApiKey) : "no key";
        return $"{host} · {settings.Models.FirstOrDefault()} · {key}";
    }

    private async void AddApiInstance()
    {
        var next = Enumerable.Range(1, 99).Select(n => $"api{n}").FirstOrDefault(n => !Directory.Exists(env.DataDirOf(n))) ?? "api";
        var name = await dialogs.Prompt("New instance with API key", "Instance name (used as the folder name)", next, n => InstanceCatalog.ValidateName(n.Trim())
            ?? (Directory.Exists(env.DataDirOf(n.Trim())) ? "This name is already in use." : null));
        if (name == null) return;
        try
        {
            var created = InstanceCatalog.Create(env, name.Trim());
            log.Info($"New instance created: {created.Id}");
            if (!await EditApi(created))
                log.Info($"No API key entered for '{created.Id}'; the instance can still be used with a normal sign-in.");
            _ = RefreshAsync();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException)
        {
            await dialogs.Alert("Could not create", e.Message);
        }
    }

    /// <summary>Asks for the gateway settings and saves them; true when saved.</summary>
    private async Task<bool> EditApi(InstanceInfo instance)
    {
        if (instance.IsMain)
        {
            await dialogs.Alert("API key", "The main Claude cannot be used with an API key. Create a new instance with 'Add with API key'.");
            return false;
        }
        var current = ApiProfile.Load(instance, keyProtector) ?? new ApiSettings(ApiProfile.OpenRouterBaseUrl, "", ApiProfile.DefaultModels);
        var running = instance.IsApi ? IsCliOpen(instance) : InstanceProbe.IsRunning(instance);
        if (!instance.IsApi && InstanceProbe.IsSignedIn(instance) && !await dialogs.Confirm("API key",
                $"'{LabelOf(instance.Id)}' is currently signed in with a claude.ai account. After switching to an API key, this instance opens as Claude Code (CLI) with the key instead of Claude Desktop; the existing sign-in data is kept but not used.\n\nContinue?"))
            return false;

        var settings = await dialogs.EditApi($"API key · {LabelOf(instance.Id)}", current);
        if (settings == null) return false;
        try
        {
            ApiProfile.Save(instance, settings, keyProtector);
            log.Info($"'{LabelOf(instance.Id)}' will connect with an API key ({ApiProfile.Mask(settings.ApiKey)}).");
            if (running)
                await dialogs.Inform("API key", "Saved. Open windows keep running with the old settings; newly opened windows use the new ones.");
            _ = RefreshAsync();
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await dialogs.Alert("Could not save", e.Message);
            return false;
        }
    }

    private async void Rename(InstanceInfo instance)
    {
        var name = await dialogs.Prompt("Rename", "Display name", LabelOf(instance.Id), n => string.IsNullOrWhiteSpace(n) ? "The name cannot be empty." : null);
        if (name == null) return;
        SaveState(s => s.Labels[instance.Id] = name.Trim());
        _ = RefreshAsync();
    }

    /// <summary>Stops the instance completely: every window and background process, tray entry included.</summary>
    private async void CloseInstance(InstanceInfo instance)
    {
        var name = LabelOf(instance.Id);
        var warning = instance.IsMain
            ? $"'{name}' will be closed completely, including its tray icon and background processes. If Claude Code is running inside it, that session ends too."
            : $"'{name}' will be closed completely, including its tray icon and background processes. Unsaved text in its windows is lost.";
        if (!await dialogs.Confirm("Close instance", warning + "\n\nContinue?")) return;
        try
        {
            if (instance.IsApi)
            {
                await Task.Run(() => CloseCliWindows(instance));
            }
            else
            {
                var stopped = await Task.Run(() => InstanceCloser.Close(env, instance));
                if (stopped == 0 && InstanceProbe.IsRunning(instance))
                {
                    await dialogs.Alert("Could not close", $"'{name}' is running, but its processes could not be stopped from here.");
                    return;
                }
            }
            log.Info($"'{name}' closed.");
            await RefreshAsync();
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            await dialogs.Alert("Could not close", e.Message);
        }
    }

    private void CloseCliWindows(InstanceInfo instance)
    {
        if (HostPlatform.IsWindows)
        {
            foreach (var process in cliWindows.TryGetValue(instance.Id, out var list) ? list.Where(p => !HasExited(p)).ToList() : [])
                process.Kill(entireProcessTree: true);
            return;
        }
        foreach (var pid in CliLauncher.OpenSessions(instance.Id))
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }
    }

    private async void Remove(InstanceInfo instance)
    {
        if (instance.IsApi && IsCliOpen(instance))
        {
            await dialogs.Alert("Could not delete", "Close this instance's Claude Code (CLI) windows first.");
            return;
        }
        if (!await dialogs.Confirm("Delete instance",
                $"All data of '{LabelOf(instance.Id)}' will be deleted (sign-in, settings, chat entries).\nChat contents and other instances are not affected.\n\nContinue?"))
            return;
        try
        {
            InstanceCatalog.Remove(env, instance);
            SaveState(s => s.Labels.Remove(instance.Id));
            log.Info($"Instance deleted: {instance.Id}");
            _ = RefreshAsync();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await dialogs.Alert("Could not delete", e.Message);
        }
    }

    private void OpenFolder(string path)
    {
        try
        {
            HostPlatform.OpenFolder(path);
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException)
        {
            _ = dialogs.Alert("Could not open the folder", $"{path}\n\n{e.Message}");
        }
    }

    // ---- sync ----

    public async Task SyncAsync()
    {
        if (IsSyncing) return;
        IsSyncing = true;
        try
        {
            var report = await Task.Run(engine.Run);
            foreach (var error in report.Errors) log.Info("Sync error: " + error);
            if (report.HasChanges)
                log.Info($"Synced: {report.ChatsCopied} chat entries, {report.SettingsKeysApplied} settings.");
            var waiting = report.InstancesWaitingForClose > 0 ? $" · {report.InstancesWaitingForClose} instance(s) will get settings once closed" : "";
            LastSyncText = $"Last sync {report.At:HH:mm:ss} · {report.Instances} instances · {report.ChatsCopied} chats, {report.SettingsKeysApplied} settings{waiting}";
            LastSyncShort = $"Last sync {report.At:HH:mm}";
            await RefreshAsync();
        }
        finally
        {
            IsSyncing = false;
        }
    }
}
