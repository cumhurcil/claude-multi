using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using ClaudeMulti.App.Infrastructure;
using ClaudeMulti.Core;
using ClaudeMulti.Core.Cache;

namespace ClaudeMulti.App.ViewModels;

public sealed class CacheRowViewModel(int number, TurnRow row)
{
    public string Number { get; } = "#" + number;
    public string Steps { get; } = row.Steps.ToString();
    public string Read { get; } = CacheMath.FmtTokens(row.Read);
    public string Write { get; } = CacheMath.FmtTokens(row.Write);
    public string Fresh { get; } = CacheMath.FmtTokens(row.Fresh);
    public string Output { get; } = CacheMath.FmtTokens(row.Output);
    public string Hit { get; } = $"{CacheMath.RowRatio(row) * 100:0}%";
    public IBrush HitBrush { get; } = CacheMath.RowRatio(row) switch { >= 0.8 => Palette.Good, >= 0.5 => Palette.Warn, _ => Palette.Bad };
}

/// <summary>
/// The prompt-cache meter (a port of the "prompt-cache-control" mod) for the newest Claude Code
/// conversation, plus the switches in Claude Code's settings.json that control prompt caching.
/// </summary>
public sealed class CacheViewModel : ObservableObject
{
    private const int SessionCount = 12;
    private const long ToastMinPromptTokens = 20_000;

    private readonly StateStore store;
    private readonly string settingsPath;
    private readonly string backupsDir;
    private readonly string projectsDir;
    private readonly Action<string> log;
    private readonly Dictionary<string, TranscriptReader> readers = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private IReadOnlyList<TranscriptInfo> sessions = [];
    private int sessionIndex;
    private bool followNewest = true;
    private CacheEnv env = new();
    private string? settingTtl;
    private DateTime lastScan = DateTime.MinValue;
    private long toastKey = -1;
    private int toastLevel = int.MaxValue;
    private CacheSettingsValues loaded = new(new CacheEnv(), null);

    private string bandText = "No conversation found yet.";
    private string clockText = "–:––";
    private string adviceText = "";
    private IBrush adviceBrush = Palette.Muted;
    private IBrush lifeBrush = Palette.Muted;
    private double lifeValue;
    private double readTokens, writeTokens, freshTokens;
    private string ttlText = "";
    private string sessionText = "";
    private string settingsStatus = "";
    private bool disableAll, disableHaiku, disableSonnet, disableOpus, enable1h, force5m;

    public CacheViewModel(StateStore store, string backupsDir, Action<string> log, string? settingsPath = null, string? projectsDir = null)
    {
        this.store = store;
        this.backupsDir = backupsDir;
        this.log = log;
        this.settingsPath = settingsPath ?? ClaudeCacheSettings.DefaultPath();
        this.projectsDir = projectsDir ?? Path.Combine(Path.GetDirectoryName(this.settingsPath)!, "projects");

        var s = store.Load();
        TtlOption = new ChoiceGroup(s.CacheTtl, ("Auto", "auto"), ("5 min", "5m"), ("1 hour", "1h"));
        AccountOption = new ChoiceGroup(s.CacheAccount, ("Other", "other"), ("Subscription", "subscription"), ("Credits", "credits"));
        EnvTtl = new ChoiceGroup("", ("None", ""), ("5 min", "5m"), ("1 hour", "1h"));
        SettingTtl = new ChoiceGroup("", ("None", ""), ("5 min", "5m"), ("1 hour", "1h"));
        warnSeconds = s.CacheWarnSeconds;
        compactAt = s.CacheCompactAtTokens;
        toast = s.CacheToast;

        TtlOption.Changed += SaveOptions;
        AccountOption.Changed += SaveOptions;
        foreach (var group in new[] { EnvTtl, SettingTtl }) group.Changed += () => RaiseDirty();

        PreviousCommand = new RelayCommand(() => MoveSession(1), () => sessions.Count > 1).Tracked();
        NextCommand = new RelayCommand(() => MoveSession(-1), () => sessions.Count > 1).Tracked();
        FollowCommand = new RelayCommand(() => { followNewest = true; sessionIndex = 0; Tick(force: true); });
        ApplyCommand = new RelayCommand(Apply, () => IsDirty).Tracked();
        ReloadCommand = new RelayCommand(LoadSettings);

        LoadSettings();
        timer.Tick += (_, _) => Tick();
        timer.Start();
        Tick(force: true);
    }

    public event Action<string, string>? ToastRaised;

    public ChoiceGroup TtlOption { get; }
    public ChoiceGroup AccountOption { get; }
    public ChoiceGroup EnvTtl { get; }
    public ChoiceGroup SettingTtl { get; }
    public ObservableCollection<CacheRowViewModel> Rows { get; } = [];

    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand FollowCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand ReloadCommand { get; }

    // ---- live meter ----
    public string BandText { get => bandText; private set => Set(ref bandText, value); }
    public string ClockText { get => clockText; private set => Set(ref clockText, value); }
    public string AdviceText { get => adviceText; private set => Set(ref adviceText, value); }
    public IBrush AdviceBrush { get => adviceBrush; private set => Set(ref adviceBrush, value); }
    public IBrush LifeBrush { get => lifeBrush; private set => Set(ref lifeBrush, value); }
    public double LifeValue { get => lifeValue; private set => Set(ref lifeValue, value); }
    public double ReadTokens { get => readTokens; private set => Set(ref readTokens, value); }
    public double WriteTokens { get => writeTokens; private set => Set(ref writeTokens, value); }
    public double FreshTokens { get => freshTokens; private set => Set(ref freshTokens, value); }
    public string TtlText { get => ttlText; private set => Set(ref ttlText, value); }
    public string SessionText { get => sessionText; private set => Set(ref sessionText, value); }
    public bool FollowingNewest => followNewest;

    // ---- meter options (saved in the manager state) ----
    private int warnSeconds;
    private long compactAt;
    private bool toast;

    public int WarnSeconds { get => warnSeconds; set { if (Set(ref warnSeconds, Math.Clamp(value, 5, 600))) SaveOptions(); } }
    public long CompactAtTokens { get => compactAt; set { if (Set(ref compactAt, Math.Clamp(value, 1_000, 2_000_000))) SaveOptions(); } }
    public bool ToastEnabled { get => toast; set { if (Set(ref toast, value)) SaveOptions(); } }

    // ---- Claude Code settings.json switches ----
    public bool DisableAll { get => disableAll; set { if (Set(ref disableAll, value)) RaiseDirty(); } }
    public bool DisableHaiku { get => disableHaiku; set { if (Set(ref disableHaiku, value)) RaiseDirty(); } }
    public bool DisableSonnet { get => disableSonnet; set { if (Set(ref disableSonnet, value)) RaiseDirty(); } }
    public bool DisableOpus { get => disableOpus; set { if (Set(ref disableOpus, value)) RaiseDirty(); } }
    public bool Enable1h { get => enable1h; set { if (Set(ref enable1h, value)) RaiseDirty(); } }
    public bool Force5m { get => force5m; set { if (Set(ref force5m, value)) RaiseDirty(); } }
    public string SettingsStatus { get => settingsStatus; private set => Set(ref settingsStatus, value); }
    public string SettingsPath => settingsPath;
    public bool IsDirty => CurrentValues() != loaded;

    private void RaiseDirty()
    {
        Raise(nameof(IsDirty));
        RelayCommand.Requery();
    }

    private CacheSettingsValues CurrentValues() => new(
        new CacheEnv(
            Enable1h: enable1h ? "1" : null,
            Force5m: force5m ? "1" : null,
            TtlVar: EnvTtl.Value == "" ? null : EnvTtl.Value,
            DisableAll: disableAll ? "1" : null,
            DisableHaiku: disableHaiku ? "1" : null,
            DisableSonnet: disableSonnet ? "1" : null,
            DisableOpus: disableOpus ? "1" : null),
        SettingTtl.Value == "" ? null : SettingTtl.Value);

    private void LoadSettings()
    {
        var values = ClaudeCacheSettings.Read(settingsPath);
        // Normalize "true"/"1" to the same on/off so the dirty check compares like with like.
        var e = values.Env;
        var normalized = new CacheEnv(
            Enable1h: CacheMath.IsOn(e.Enable1h) ? "1" : null,
            Force5m: CacheMath.IsOn(e.Force5m) ? "1" : null,
            TtlVar: CacheMath.ParseTtl(e.TtlVar) != null ? e.TtlVar : null,
            DisableAll: CacheMath.IsOn(e.DisableAll) ? "1" : null,
            DisableHaiku: CacheMath.IsOn(e.DisableHaiku) ? "1" : null,
            DisableSonnet: CacheMath.IsOn(e.DisableSonnet) ? "1" : null,
            DisableOpus: CacheMath.IsOn(e.DisableOpus) ? "1" : null);
        var ttl = CacheMath.ParseTtl(values.PromptCacheTtl) != null ? values.PromptCacheTtl : null;

        loaded = new CacheSettingsValues(normalized, ttl);
        env = values.Env;
        settingTtl = values.PromptCacheTtl;

        DisableAll = normalized.DisableAll != null;
        DisableHaiku = normalized.DisableHaiku != null;
        DisableSonnet = normalized.DisableSonnet != null;
        DisableOpus = normalized.DisableOpus != null;
        Enable1h = normalized.Enable1h != null;
        Force5m = normalized.Force5m != null;
        EnvTtl.Value = normalized.TtlVar ?? "";
        SettingTtl.Value = ttl ?? "";
        RaiseDirty();
        SettingsStatus = File.Exists(settingsPath) ? "Current settings loaded." : "settings.json does not exist yet; it is created when you apply.";
    }

    private void Apply()
    {
        try
        {
            var backup = ClaudeCacheSettings.Write(settingsPath, backupsDir, CurrentValues(), DateTime.UtcNow);
            log($"Claude settings updated{(backup != null ? $" (backup: {Path.GetFileName(backup)})" : "")}");
            LoadSettings();
            SettingsStatus = "Applied. Newly opened Claude Code sessions use these settings; open sessions keep the old ones until restarted.";
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            SettingsStatus = "Could not apply: " + e.Message;
            log("Could not write Claude settings: " + e.Message);
        }
    }

    private void SaveOptions() =>
        store.Update(s =>
        {
            s.CacheTtl = TtlOption.Value;
            s.CacheAccount = AccountOption.Value;
            s.CacheWarnSeconds = warnSeconds;
            s.CacheCompactAtTokens = compactAt;
            s.CacheToast = toast;
        });

    private CacheOptions Options() => new(
        CacheMath.ParseTtl(TtlOption.Value), warnSeconds, compactAt, toast);

    private Account AccountKind() => AccountOption.Value switch
    {
        "subscription" => Account.Subscription,
        "credits" => Account.Credits,
        _ => Account.Other,
    };

    private void MoveSession(int step)
    {
        followNewest = false;
        sessionIndex = Math.Clamp(sessionIndex + step, 0, Math.Max(0, sessions.Count - 1));
        Tick(force: true);
    }

    // ---- refresh ----

    private void Tick(bool force = false)
    {
        if (force || DateTime.UtcNow - lastScan > TimeSpan.FromSeconds(10)) Rescan();
        if (sessions.Count == 0) { ShowEmpty(); return; }
        if (followNewest) sessionIndex = 0;
        sessionIndex = Math.Clamp(sessionIndex, 0, sessions.Count - 1);

        var info = sessions[sessionIndex];
        if (!readers.TryGetValue(info.Path, out var reader)) readers[info.Path] = reader = new TranscriptReader(info.Path);
        reader.Refresh();

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var view = CacheAnalyzer.Analyze(reader.Samples, Options(), env, settingTtl, AccountKind(), now);
        Show(view, reader, info);
        MaybeToast(view);
    }

    private void Rescan()
    {
        lastScan = DateTime.UtcNow;
        sessions = TranscriptFinder.Recent(projectsDir, SessionCount);
        RelayCommand.Requery();
        var keep = sessions.Select(s => s.Path).ToHashSet();
        foreach (var stale in readers.Keys.Where(k => !keep.Contains(k)).ToList()) readers.Remove(stale);
        var settings = ClaudeCacheSettings.Read(settingsPath);
        if (!IsDirty) { env = settings.Env; settingTtl = settings.PromptCacheTtl; }
        Raise(nameof(FollowingNewest));
    }

    private void ShowEmpty()
    {
        BandText = "No conversation found yet.";
        ClockText = "–:––";
        AdviceText = "Shows up here once you start a conversation with Claude Code.";
        AdviceBrush = Palette.Muted;
        LifeValue = 0;
        SessionText = "";
        Rows.Clear();
    }

    private void Show(CacheView view, TranscriptReader reader, TranscriptInfo info)
    {
        var last = view.Last;
        BandText = last == null
            ? "No usage recorded in this conversation yet."
            : $"{view.HitPercent:0}% hit · read {CacheMath.FmtTokens(last.Read)} · written {CacheMath.FmtTokens(last.Write)} · new {CacheMath.FmtTokens(last.Fresh)}";
        ClockText = last == null || view.LeftMs <= 0 ? "0:00" : CacheMath.FmtClock(view.LeftMs);
        TtlText = $"{CacheMath.TtlLabel(view.Ttl)} · source: {view.TtlSource}";
        AdviceText = view.Advice.Text;
        AdviceBrush = view.Advice.Kind switch
        {
            AdviceKind.Warm => Palette.Good,
            AdviceKind.Soon => Palette.Warn,
            AdviceKind.Expired or AdviceKind.Miss => Palette.Bad,
            _ => Palette.Muted,
        };
        LifeValue = view.LifeRatio;
        LifeBrush = view.Color switch { LifeColor.Green => Palette.Good, LifeColor.Yellow => Palette.Warn, _ => Palette.Bad };
        ReadTokens = last?.Read ?? 0;
        WriteTokens = last?.Write ?? 0;
        FreshTokens = last?.Fresh ?? 0;

        var project = reader.Cwd != null ? Path.GetFileName(reader.Cwd.TrimEnd('\\', '/')) : info.Project;
        var age = DateTime.UtcNow - info.LastWriteUtc;
        SessionText = $"{(followNewest ? "Latest conversation" : $"Conversation {sessionIndex + 1}/{sessions.Count}")} · {project} · {Ago(age)}";

        Rows.Clear();
        var start = view.Rows.Count;
        foreach (var row in view.Rows.Reverse()) Rows.Add(new CacheRowViewModel(start--, row));
        Raise(nameof(FollowingNewest));
    }

    private void MaybeToast(CacheView view)
    {
        var last = view.Last;
        if (last == null || !toast) return;
        if (toastKey != last.StartedAt) { toastKey = last.StartedAt; toastLevel = int.MaxValue; }
        if (CacheMath.PromptTokens(last) < ToastMinPromptTokens || view.LeftMs <= 0) return;

        var secs = (int)Math.Ceiling(view.LeftMs / 1000.0);
        if (CacheMath.NextToastMark(secs, warnSeconds, toastLevel) is not { } mark) return;
        toastLevel = mark;
        var tail = secs <= 10
            ? "send a message now"
            : $"send a message to keep {CacheMath.FmtTokens(CacheMath.PromptTokens(last))} tokens warm";
        ToastRaised?.Invoke("Prompt cache about to expire", $"{(secs >= 60 ? CacheMath.FmtClock(view.LeftMs) : secs + " s")} left: {tail}.");
    }

    private static string Ago(TimeSpan age) => age.TotalSeconds < 60 ? "just now"
        : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} min ago"
        : age.TotalHours < 24 ? $"{(int)age.TotalHours} h ago"
        : $"{(int)age.TotalDays} d ago";
}
