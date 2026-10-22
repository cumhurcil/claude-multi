using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeMulti.Core;
using ClaudeMulti.Core.Cache;
using ClaudeMulti.Core.Transfer;

// Plain console tests (no NuGet needed). Exit code = number of failures.
var failures = 0;
var total = 0;

void Test(string name, Action body)
{
    total++;
    var root = Path.Combine(Path.GetTempPath(), "claudemulti-test-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(root);
        Environment.CurrentDirectory = Path.GetTempPath();
        TestContext.Root = root;
        body();
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception e)
    {
        failures++;
        Console.WriteLine($"FAIL  {name}\n      {e.GetType().Name}: {e.Message}");
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}

void WindowsTest(string name, Action body)
{
    if (OperatingSystem.IsWindows()) { Test(name, body); return; }
    Console.WriteLine($"SKIP  {name} (Windows only)");
}

void UnixTest(string name, Action body)
{
    if (!OperatingSystem.IsWindows()) { Test(name, body); return; }
    Console.WriteLine($"SKIP  {name} (macOS/Linux only)");
}

// ---- catalog ----------------------------------------------------------------
Test("catalog: lists main first then instances by name", () =>
{
    var env = TestContext.Env();
    Directory.CreateDirectory(env.MainDataDir);
    InstanceCatalog.Create(env, "b");
    InstanceCatalog.Create(env, "a");
    var ids = InstanceCatalog.List(env).Select(i => i.Id).ToArray();
    Check.Equal("main,a,b", string.Join(",", ids));
});

Test("catalog: rejects bad and reserved names", () =>
{
    foreach (var bad in new[] { "", "..", "a/b", "a b", "main", "backups", "-x", new string('a', 33) })
        Check.True(InstanceCatalog.ValidateName(bad) != null, $"'{bad}' should be rejected");
    Check.True(InstanceCatalog.ValidateName("work_2") == null, "work_2 is valid");
});

Test("catalog: remove deletes only an extra instance folder", () =>
{
    var env = TestContext.Env();
    Directory.CreateDirectory(env.MainDataDir);
    var inst = InstanceCatalog.Create(env, "x");
    File.WriteAllText(Path.Combine(inst.DataDir, "f.txt"), "1");
    InstanceCatalog.Remove(env, inst);
    Check.True(!Directory.Exists(inst.DataDir), "folder removed");
    Check.Throws<InvalidOperationException>(() => InstanceCatalog.Remove(env, new InstanceInfo("main", env.MainDataDir)));
    Check.True(Directory.Exists(env.MainDataDir), "main untouched");
});

Test("catalog: remove refuses a folder outside the instances root", () =>
{
    var env = TestContext.Env();
    var outside = Path.Combine(TestContext.Root, "elsewhere");
    Directory.CreateDirectory(outside);
    Check.Throws<InvalidOperationException>(() => InstanceCatalog.Remove(env, new InstanceInfo("evil", outside)));
    Check.True(Directory.Exists(outside), "outside folder untouched");
});

WindowsTest("probe: running while lockfile is held open", () =>
{
    var env = TestContext.Env();
    var inst = InstanceCatalog.Create(env, "p");
    Check.True(!InstanceProbe.IsRunning(inst), "no lockfile -> not running");
    using var held = new FileStream(inst.LockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
    Check.True(InstanceProbe.IsRunning(inst), "held lockfile -> running");
});

Test("probe: unsafe account id from config is ignored", () =>
{
    var env = TestContext.Env();
    var inst = InstanceCatalog.Create(env, "p");
    TestContext.WriteConfig(inst, new JsonObject { ["lastKnownAccountUuid"] = "..\\..\\evil" });
    Check.True(InstanceProbe.SignedInAccount(inst) == null, "path traversal rejected");
});

// ---- chat sync --------------------------------------------------------------
const string AccA = "aaaaaaaa-0000-0000-0000-000000000001";
const string AccB = "bbbbbbbb-0000-0000-0000-000000000002";
const string Org = "oooooooo-0000-0000-0000-000000000003";

Test("chats: copies missing chats across accounts and instances, never deletes", () =>
{
    var (main, two) = TestContext.TwoInstances();
    TestContext.AddChat(main, AccA, Org, "1", 100);
    TestContext.AddChat(two, AccB, Org, "2", 200);
    var copied = ChatSync.Run([main, two]);
    Check.True(copied > 0, "something copied");
    // Each instance's own account folder must now list both chats.
    foreach (var (inst, acc) in new[] { (main, AccA), (two, AccB) })
        foreach (var id in new[] { "1", "2" })
            Check.True(File.Exists(TestContext.ChatPath(inst, acc, Org, id)), $"{inst.Id}/{acc[..1]}/{id} present");
    Check.Equal(0, ChatSync.Run([main, two]));
});

Test("chats: newer copy replaces older, older never replaces newer", () =>
{
    var (main, two) = TestContext.TwoInstances();
    TestContext.AddChat(main, AccA, Org, "1", 100);
    TestContext.AddChat(two, AccB, Org, "1", 300);
    ChatSync.Run([main, two]);
    var json = JsonFile.ReadOrNull(TestContext.ChatPath(main, AccA, Org, "1"))!;
    Check.Equal(300L, json["lastActivityAt"]!.GetValue<long>());
});

Test("chats: a deleted chat is not brought back", () =>
{
    var (main, two) = TestContext.TwoInstances();
    TestContext.AddChat(main, AccA, Org, "9", 100);
    TestContext.AddChat(two, AccB, Org, "1", 100);
    File.WriteAllText(Path.Combine(two.SessionsDir, AccB, Org, "deleted_9"), "");
    ChatSync.Run([main, two]);
    Check.True(!File.Exists(TestContext.ChatPath(two, AccB, Org, "9")), "deleted chat not copied");
    Check.True(File.Exists(TestContext.ChatPath(main, AccA, Org, "9")), "source kept");
});

Test("chats: a freshly signed-in account gets every chat", () =>
{
    var (main, two) = TestContext.TwoInstances();
    TestContext.AddChat(main, AccA, Org, "1", 100);
    TestContext.WriteConfig(two, new JsonObject { ["lastKnownAccountUuid"] = AccB, ["oauth:tokenCacheV2"] = "x" });
    ChatSync.Run([main, two]);
    Check.True(File.Exists(TestContext.ChatPath(two, AccB, Org, "1")), "new account sees chat");
    Check.Equal(1, InstanceProbe.ChatCount(two));
});

// ---- settings sync ----------------------------------------------------------
static bool Never(InstanceInfo _) => false;

SettingsSyncResult SyncSettings(InstanceInfo[] all, Func<InstanceInfo, bool>? running = null) =>
    SettingsSync.Run(all, running ?? Never, Path.Combine(TestContext.Root, "rec.json"), Path.Combine(TestContext.Root, "bk"), DateTime.UtcNow);

Test("settings: never copies tokens, account id or machine state", () =>
{
    var (main, two) = TestContext.TwoInstances();
    TestContext.WriteConfig(main, new JsonObject
    {
        ["oauth:tokenCacheV2"] = "SECRET", ["oauth:tokenCache"] = "SECRET2", ["lastKnownAccountUuid"] = AccA,
        ["quickWindowPosition"] = "x", ["userThemeMode"] = "dark", ["locale"] = "tr-TR",
    });
    TestContext.WriteConfig(two, new JsonObject { ["lastKnownAccountUuid"] = AccB });
    SyncSettings([main, two]);
    var cfg = JsonFile.ReadOrNull(two.ConfigPath)!;
    Check.Equal("dark", cfg["userThemeMode"]!.GetValue<string>());
    Check.Equal("tr-TR", cfg["locale"]!.GetValue<string>());
    Check.True(!cfg.ContainsKey("oauth:tokenCacheV2") && !cfg.ContainsKey("oauth:tokenCache"), "tokens not copied");
    Check.True(!cfg.ContainsKey("quickWindowPosition"), "window position not copied");
    Check.Equal(AccB, cfg["lastKnownAccountUuid"]!.GetValue<string>());
});

Test("settings: a change made later wins and is not reverted by a sibling", () =>
{
    var (main, two) = TestContext.TwoInstances();
    TestContext.WriteConfig(main, new JsonObject { ["userThemeMode"] = "dark" });
    TestContext.WriteConfig(two, new JsonObject { ["userThemeMode"] = "dark" });
    SyncSettings([main, two]);

    TestContext.WriteConfig(two, new JsonObject { ["userThemeMode"] = "light" }, merge: true);
    File.SetLastWriteTimeUtc(two.ConfigPath, DateTime.UtcNow.AddSeconds(-5));
    // main is touched later for an unrelated reason (e.g. a token refresh) but its theme is unchanged
    File.SetLastWriteTimeUtc(main.ConfigPath, DateTime.UtcNow);
    SyncSettings([main, two]);
    Check.Equal("light", JsonFile.ReadOrNull(main.ConfigPath)!["userThemeMode"]!.GetValue<string>());
    Check.Equal("light", JsonFile.ReadOrNull(two.ConfigPath)!["userThemeMode"]!.GetValue<string>());
});

Test("settings: a running instance is not written until it is closed", () =>
{
    var (main, two) = TestContext.TwoInstances();
    TestContext.WriteConfig(main, new JsonObject { ["locale"] = "en-US" });
    TestContext.WriteConfig(two, new JsonObject { ["locale"] = "en-US" });
    SyncSettings([main, two]);

    TestContext.WriteConfig(main, new JsonObject { ["locale"] = "tr-TR" }, merge: true);
    var result = SyncSettings([main, two], i => i.Id == "two");
    Check.Equal("en-US", JsonFile.ReadOrNull(two.ConfigPath)!["locale"]!.GetValue<string>());
    Check.Equal(1, result.PendingInstances);

    SyncSettings([main, two]);
    Check.Equal("tr-TR", JsonFile.ReadOrNull(two.ConfigPath)!["locale"]!.GetValue<string>());
});

Test("settings: MCP config (claude_desktop_config.json) syncs and backs up", () =>
{
    var (main, two) = TestContext.TwoInstances();
    File.WriteAllText(Path.Combine(main.DataDir, "claude_desktop_config.json"), """{"mcpServers":{"a":{"command":"x"}}}""");
    File.WriteAllText(Path.Combine(two.DataDir, "claude_desktop_config.json"), """{"other":1}""");
    SyncSettings([main, two]);
    var cfg = JsonFile.ReadOrNull(Path.Combine(two.DataDir, "claude_desktop_config.json"))!;
    Check.Equal("x", cfg["mcpServers"]!["a"]!["command"]!.GetValue<string>());
    Check.Equal(1, cfg["other"]!.GetValue<int>());
    Check.True(Directory.GetFiles(Path.Combine(TestContext.Root, "bk"), "claude_desktop_config.json", SearchOption.AllDirectories).Length > 0, "backup made");
});

Test("settings: an unreadable config is left alone", () =>
{
    var (main, two) = TestContext.TwoInstances();
    TestContext.WriteConfig(main, new JsonObject { ["locale"] = "tr-TR" });
    File.WriteAllText(two.ConfigPath, "{ not json");
    SyncSettings([main, two]);
    Check.Equal("{ not json", File.ReadAllText(two.ConfigPath));
});

Test("settings: second run changes nothing", () =>
{
    var (main, two) = TestContext.TwoInstances();
    TestContext.WriteConfig(main, new JsonObject { ["locale"] = "tr-TR", ["userThemeMode"] = "dark" });
    SyncSettings([main, two]);
    var again = SyncSettings([main, two]);
    Check.Equal(0, again.KeysApplied);
    Check.Equal(0, again.FilesWritten);
});

// ---- protocol routing -------------------------------------------------------
Test("store app: package name maps to its AppUserModelId, other packages do not", () =>
{
    Check.Equal("Claude_pzs8sxrjxfjjc!Claude", StoreApp.AppIdOf("Claude_2.42304.0.0_x64__pzs8sxrjxfjjc"));
    Check.Equal<string?>(null, StoreApp.AppIdOf("ClaudeHelper_1.0.0.0_x64__abc123"));
    Check.Equal<string?>(null, StoreApp.AppIdOf("Microsoft.WindowsStore_22.0.0.0_x64__8wekyb3d8bbwe"));
});

Test("store app: arguments are quoted so a data folder with spaces stays one argument", () =>
{
    Check.Equal("--user-data-dir=C:\\a", StoreApp.JoinArguments(["--user-data-dir=C:\\a"]));
    Check.Equal("\"--user-data-dir=C:\\a b\" claude://x", StoreApp.JoinArguments(["--user-data-dir=C:\\a b", "claude://x"]));
    Check.Equal("\"say \\\"hi\\\"\"", StoreApp.JoinArguments(["say \"hi\""]));
});

Test("router: link goes to the pending sign-in, else to main", () =>
{
    var (main, two) = TestContext.TwoInstances();
    var list = new[] { main, two };
    var now = DateTimeOffset.UtcNow;
    Check.Equal("main", ProtocolRouter.ResolveTarget(new ManagerState(), list, now)!.Id);
    var pending = new ManagerState { PendingLoginId = "two", PendingSince = now.AddMinutes(-1) };
    Check.Equal("two", ProtocolRouter.ResolveTarget(pending, list, now)!.Id);
    var stale = new ManagerState { PendingLoginId = "two", PendingSince = now.AddMinutes(-30) };
    Check.Equal("main", ProtocolRouter.ResolveTarget(stale, list, now)!.Id);
    var gone = new ManagerState { PendingLoginId = "deleted", PendingSince = now };
    Check.Equal("main", ProtocolRouter.ResolveTarget(gone, list, now)!.Id);
});

Test("router: only well-formed claude:// links are accepted", () =>
{
    Check.True(ProtocolRouter.IsClaudeUri("claude://login/callback?code=abc"), "valid");
    foreach (var bad in new[] { null, "", "claude://", "https://x", "--user-data-dir=C:\\x", "claude://a\nb", "claude://" + new string('a', 9000) })
        Check.True(!ProtocolRouter.IsClaudeUri(bad), $"'{bad?[..Math.Min(20, bad.Length)]}' rejected");
});

Test("router: enable keeps the original handler, disable restores it, reassert fixes drift", () =>
{
    var env = TestContext.Env();
    var store = new StateStore(env.StatePath);
    var reg = new FakeRegistry { Command = "\"C:\\Claude\\claude.exe\" \"%1\"" };
    var router = new ProtocolRouter(reg, store, "C:\\Mgr\\ClaudeMulti.exe");

    router.Enable();
    Check.True(router.IsHooked, "hooked");
    Check.Equal("\"C:\\Claude\\claude.exe\" \"%1\"", store.Load().OriginalProtocolCommand);
    router.Enable();
    Check.Equal("\"C:\\Claude\\claude.exe\" \"%1\"", store.Load().OriginalProtocolCommand);

    reg.Command = "\"C:\\Claude\\app-2\\claude.exe\" \"%1\"";
    Check.True(router.Reassert(), "reasserted");
    Check.True(router.IsHooked, "hooked again");
    Check.Equal("\"C:\\Claude\\app-2\\claude.exe\" \"%1\"", store.Load().OriginalProtocolCommand);

    router.Disable();
    Check.Equal("\"C:\\Claude\\app-2\\claude.exe\" \"%1\"", reg.Command);
    Check.True(!store.Load().HookEnabled, "disabled");
    Check.True(!router.Reassert(), "no reassert when disabled");
});

Test("router: a package-owned claude: handler is not 'routed here' until the user picks the manager", () =>
{
    var env = TestContext.Env();
    var reg = new FakeRegistry { Command = "\"C:\\Claude\\claude.exe\" \"%1\"", ProgId = "AppXaem4n1tckgw588q10avtdbzpbgt71c77" };
    var router = new ProtocolRouter(reg, new StateStore(env.StatePath), "C:\\Mgr\\ClaudeMulti.exe");

    router.Enable();
    Check.True(reg.HandlerRegistered, "manager listed as a handler");
    Check.True(router.IsHooked, "registry key is ours");
    Check.True(!router.IsRoutedHere, "Windows still opens the package");

    reg.ProgId = ProtocolRouter.HandlerProgId;
    Check.True(router.IsRoutedHere, "user picked the manager");

    reg.ProgId = "claude";
    Check.True(router.IsRoutedHere, "classic key that is ours");
    reg.Command = "\"C:\\Claude\\claude.exe\" \"%1\"";
    Check.True(!router.IsRoutedHere, "classic key taken back by Claude");

    reg.ProgId = null;
    reg.Command = router.HookCommand;
    Check.True(router.IsRoutedHere, "unknown ProgId falls back to the registry key");
});

WindowsTest("closer: finds the root process of exactly one instance", () =>
{
    var main = new InstanceInfo(ClaudeEnvironment.MainId, @"C:\Users\me\AppData\Local\Packages\Claude\LocalCache\Roaming\Claude");
    var two = new InstanceInfo("2", @"C:\Users\me\ClaudeInstances\2");
    var spaced = new InstanceInfo("3", @"C:\Users\me\My Instances\3");
    const string exe = "\"C:\\Program Files\\WindowsApps\\Claude_2.0_x64__x\\app\\Claude.exe\"";

    Check.True(InstanceCloser.IsRootOf(exe, main), "main has no --user-data-dir");
    Check.True(!InstanceCloser.IsRootOf(exe, two), "plain start is not instance 2");
    Check.True(InstanceCloser.IsRootOf(exe + @" --user-data-dir=C:\Users\me\ClaudeInstances\2", two), "instance 2");
    Check.True(InstanceCloser.IsRootOf(exe + @" --user-data-dir=C:\users\me\claudeinstances\2\", two), "case and trailing slash do not matter");
    Check.True(!InstanceCloser.IsRootOf(exe + @" --user-data-dir=C:\Users\me\ClaudeInstances\2", main), "instance 2 is not main");
    Check.True(!InstanceCloser.IsRootOf(exe + @" --user-data-dir=C:\Users\me\ClaudeInstances\22", two), "22 is not 2");
    Check.True(InstanceCloser.IsRootOf(exe + " --user-data-dir=\"C:\\Users\\me\\My Instances\\3\"", spaced), "quoted path with spaces");
    Check.True(!InstanceCloser.IsRootOf(exe + @" --type=renderer --user-data-dir=C:\Users\me\ClaudeInstances\2", two), "child processes are not roots");
    Check.True(!InstanceCloser.IsRootOf(null, main) && !InstanceCloser.IsRootOf("", main), "no command line, no match");
});

WindowsTest("closer: only Claude Desktop's exe counts, never the Claude Code CLI", () =>
{
    var packaged = new ClaudeEnvironment("m", "i", null, null, "Claude_x!Claude");
    Check.True(InstanceCloser.IsDesktopExecutable(@"C:\Program Files\WindowsApps\Claude_2.0_x64__x\app\Claude.exe", packaged), "store build");
    Check.True(!InstanceCloser.IsDesktopExecutable(@"C:\Users\me\AppData\Roaming\Claude\claude-code\2.1\abc\claude.exe", packaged), "CLI");
    var direct = new ClaudeEnvironment("m", "i", @"C:\Users\me\AppData\Local\Programs\Claude\Claude.exe");
    Check.True(InstanceCloser.IsDesktopExecutable(@"c:\users\me\appdata\local\programs\claude\claude.exe", direct), "direct install");
    Check.True(!InstanceCloser.IsDesktopExecutable(@"C:\Users\me\AppData\Roaming\Claude\claude-code\2.1\abc\claude.exe", direct), "CLI next to direct install");
});

WindowsTest("closer: reads the command line of a running process", () =>
{
    using var me = System.Diagnostics.Process.GetCurrentProcess();
    var line = InstanceCloser.CommandLineOf(me);
    Check.True(line != null && line.Contains("SelfTest", StringComparison.OrdinalIgnoreCase), "own command line mentions the test exe");
});

Test("state: concurrent updates from several threads all land", () =>
{
    var env = TestContext.Env();
    var store = new StateStore(env.StatePath);
    Parallel.For(0, 12, n => store.Update(s => s.Labels[$"k{n}"] = "v"));
    Check.Equal(12, store.Load().Labels.Count);
});

// ---- prompt cache -----------------------------------------------------------
Sample S(long at, long read, long write, long fresh, string model = "claude-sonnet-5-5", string turn = "t", Ttl? written = null) =>
    new(turn, model, at, read, write, fresh, 10, written);

Test("cache: hit ratio and token formatting", () =>
{
    Check.Equal(0.8, CacheMath.HitRatio(S(0, 80, 10, 10)));
    Check.Equal(0.0, CacheMath.HitRatio(S(0, 0, 0, 0)));
    Check.Equal("999", CacheMath.FmtTokens(999));
    Check.Equal("12.5k", CacheMath.FmtTokens(12_500));
    Check.Equal("12k", CacheMath.FmtTokens(12_000));
    Check.Equal("250k", CacheMath.FmtTokens(250_000));
    Check.Equal("1.5M", CacheMath.FmtTokens(1_500_000));
    Check.Equal("3:41", CacheMath.FmtClock(220_100));
    Check.Equal("0:00", CacheMath.FmtClock(-5));
    Check.Equal("1:00:00", CacheMath.FmtClock(3_600_000));
});

Test("cache: lifetime rules apply in order", () =>
{
    var none = new CacheEnv();
    Check.Equal(Ttl.OneHour, CacheMath.DecideTtl(Ttl.OneHour, new CacheEnv(Force5m: "1"), "5m", Account.Other).Ttl);
    Check.Equal(Ttl.FiveMinutes, CacheMath.DecideTtl(null, new CacheEnv(Force5m: "1", TtlVar: "1h"), null, Account.Subscription).Ttl);
    Check.Equal(Ttl.OneHour, CacheMath.DecideTtl(null, new CacheEnv(TtlVar: "1h"), "5m", Account.Other).Ttl);
    Check.Equal(Ttl.OneHour, CacheMath.DecideTtl(null, none, "1h", Account.Other).Ttl);
    Check.Equal(Ttl.OneHour, CacheMath.DecideTtl(null, new CacheEnv(Enable1h: "true"), null, Account.Other).Ttl);
    Check.Equal(Ttl.OneHour, CacheMath.DecideTtl(null, none, null, Account.Subscription).Ttl);
    Check.Equal(Ttl.FiveMinutes, CacheMath.DecideTtl(null, none, null, Account.Credits).Ttl);
    Check.Equal(Ttl.FiveMinutes, CacheMath.DecideTtl(null, none, "garbage", Account.Other).Ttl);
});

Test("cache: a hit after 5+ minutes proves one hour and sticks", () =>
{
    const long min = 60_000;
    var prev = S(0, 80_000, 1_000, 500);
    var hit = S(20 * min, 80_000, 500, 0);
    Check.Equal(Ttl.OneHour, CacheMath.ObserveTtl(prev, hit, null));
    var miss = S(40 * min, 0, 81_000, 0);
    Check.Equal(Ttl.OneHour, CacheMath.ObserveTtl(hit, miss, Ttl.OneHour));
});

Test("cache: a lapse says 5 minutes even against a 1-hour default; a later hit overrules", () =>
{
    const long min = 60_000;
    var prev = S(0, 80_000, 1_000, 500);
    var miss = S(7 * min, 0, 81_000, 0);
    Check.Equal(Ttl.FiveMinutes, CacheMath.ObserveTtl(prev, miss, null));
    Check.Equal(Ttl.OneHour, CacheMath.ObserveTtl(miss, S(20 * min, 80_000, 500, 0), Ttl.FiveMinutes));

    // end to end: an account whose default is 1 hour, but whose cache visibly lapsed after 7 minutes
    var view = CacheAnalyzer.Analyze([prev, miss], new CacheOptions(), new CacheEnv(), null, Account.Subscription, 8 * min);
    Check.Equal(Ttl.FiveMinutes, view.Ttl);
});

Test("cache: traffic says nothing when inconclusive", () =>
{
    const long min = 60_000;
    var prev = S(0, 80_000, 1_000, 500);
    Check.True(CacheMath.ObserveTtl(prev, S(2 * min, 0, 81_000, 0), null) == null, "inside 5 minutes");
    Check.True(CacheMath.ObserveTtl(prev, S(20 * min, 0, 81_000, 0, model: "other"), null) == null, "model changed");
    Check.True(CacheMath.ObserveTtl(prev, S(20 * min, 0, 5_000, 100), null) == null, "prompt shrank (/compact)");
    Check.True(CacheMath.ObserveTtl(S(0, 0, 0, 5), S(20 * min, 100, 0, 0), null) == null, "nothing was cached before");
    Check.True(CacheMath.ObserveTtl(prev, S(2 * min, 80_000, 0, 0), null) == null, "an early hit proves nothing");
});

Test("cache: miss causes", () =>
{
    var prev = S(0, 90, 10, 0);
    Check.True(CacheMath.MissReason(null, prev, Ttl.OneHour) == null, "first request");
    Check.True(CacheMath.MissReason(prev, S(1000, 95, 5, 0), Ttl.OneHour) == null, "hit");
    Check.True(CacheMath.MissReason(prev, S(1000, 0, 20, 0), Ttl.OneHour) == null, "shrunk prompt is /compact");
    Check.True(CacheMath.MissReason(prev, S(1000, 0, 100, 0, model: "m2"), Ttl.OneHour)!.Contains("model"), "model");
    Check.True(CacheMath.MissReason(prev, S(400_000, 0, 100, 0), Ttl.FiveMinutes)!.Contains("had expired"), "lapsed");
    Check.True(CacheMath.MissReason(prev, S(1000, 0, 100, 0), Ttl.FiveMinutes)!.Contains("start of the prompt"), "prefix");
});

Test("cache: countdown, color and toast marks", () =>
{
    var s = S(1_000_000, 100, 0, 0);
    Check.Equal(300_000L - 1000, CacheMath.RemainingMs(s, Ttl.FiveMinutes, 1_001_000));
    Check.Equal(0L, CacheMath.RemainingMs(s, Ttl.FiveMinutes, 9_999_999));
    Check.Equal(0L, CacheMath.RemainingMs(S(0, 0, 0, 100), Ttl.FiveMinutes, 1));
    Check.Equal(LifeColor.Green, CacheMath.LifeColorOf(250_000, Ttl.FiveMinutes, 60_000));
    Check.Equal(LifeColor.Yellow, CacheMath.LifeColorOf(100_000, Ttl.FiveMinutes, 60_000));
    Check.Equal(LifeColor.Red, CacheMath.LifeColorOf(60_000, Ttl.FiveMinutes, 60_000));
    Check.Equal(60, CacheMath.NextToastMark(59, 60, int.MaxValue));
    Check.Equal(10, CacheMath.NextToastMark(9, 60, 60));
    Check.Equal(1, CacheMath.NextToastMark(1, 60, 60)); // a late tick jumps to the newest mark
    Check.True(CacheMath.NextToastMark(30, 60, 60) == null, "nothing new");
    Check.True(CacheMath.NextToastMark(30, 20, int.MaxValue) == null, "above the warning threshold");
});

Test("cache: advice follows the documented order", () =>
{
    var policy = new Policy(Ttl.FiveMinutes, 60_000, 100_000);
    var last = S(0, 150_000, 0, 0);
    Check.Equal(AdviceKind.Off, CacheMath.Advise(last, null, policy, 0, true).Kind);
    Check.Equal(AdviceKind.Cold, CacheMath.Advise(null, null, policy, 0, false).Kind);
    Check.Equal(AdviceKind.Uncached, CacheMath.Advise(S(0, 0, 0, 5), null, policy, 0, false).Kind);
    Check.Equal(AdviceKind.Warm, CacheMath.Advise(last, null, policy, 1000, false).Kind);
    Check.Equal(AdviceKind.Soon, CacheMath.Advise(last, null, policy, 250_000, false).Kind);
    var expired = CacheMath.Advise(last, null, policy, 400_000, false);
    Check.Equal(AdviceKind.Expired, expired.Kind);
    Check.True(expired.Text.Contains("/compact"), "big prompt suggests /compact");
    Check.True(!CacheMath.Advise(S(0, 5_000, 0, 0), null, policy, 400_000, false).Text.Contains("/compact"), "small prompt does not");
    var missed = CacheMath.Advise(S(1000, 0, 100, 0), S(0, 90, 10, 0), policy, 2000, false);
    Check.Equal(AdviceKind.Miss, missed.Kind);
});

Test("cache: matches the original mod's own test expectations", () =>
{
    Check.Equal("84.2k", CacheMath.FmtTokens(84_200));
    Check.Equal("182k", CacheMath.FmtTokens(182_000));
    Check.Equal("1.2M", CacheMath.FmtTokens(1_200_000));
    Check.Equal("3:20", CacheMath.FmtClock(200_000));
    Check.Equal("58:20", CacheMath.FmtClock(3_500_000));
    Check.Equal("0:01", CacheMath.FmtClock(1));
    Check.Equal(982, (int)Math.Round(CacheMath.HitRatio(S(0, 80_000, 1_000, 500)) * 1000));

    // toasts: at the threshold, then 10, 3, 2, 1 seconds, once each
    var level = int.MaxValue;
    var fired = new List<int>();
    for (var secs = 70; secs >= 1; secs--)
        if (CacheMath.NextToastMark(secs, 60, level) is { } m) { fired.Add(secs); level = m; }
    Check.Equal("60,10,3,2,1", string.Join(",", fired));
    Check.Equal(2, CacheMath.NextToastMark(2, 60, int.MaxValue));
    Check.Equal(5, CacheMath.NextToastMark(5, 5, int.MaxValue));
    Check.True(CacheMath.NextToastMark(30, 5, int.MaxValue) == null, "short warning drops early marks");

    var seg = CacheMath.Segments(113_000, 4_000, 2, 48);
    Check.Equal(48, seg.Sum());
    Check.True(seg[2] >= 1, "small part stays visible");

    var policy = new Policy(Ttl.OneHour, 60_000, 100_000);
    Check.Equal(AdviceKind.Warm, CacheMath.Advise(S(0, 80_000, 1_000, 500), null, policy, 1_000_000, false).Kind);
});

Test("cache: stacked bar cells always add up to the width", () =>
{
    foreach (var (r, w, f, width) in new[] { (90L, 5L, 5L, 20), (1L, 1L, 1L, 10), (100L, 0L, 0L, 12), (0L, 0L, 0L, 10), (7L, 3L, 0L, 33) })
    {
        var cells = CacheMath.Segments(r, w, f, width);
        Check.Equal(r + w + f == 0 ? 0 : width, cells.Sum());
    }
    Check.Equal(3, CacheMath.Segments(1, 1, 1, 3).Count(c => c == 1));
});

Test("cache: turns are grouped by adjacent turn id", () =>
{
    var rows = CacheMath.ByTurn([S(0, 1, 2, 3, turn: "a"), S(1, 1, 2, 3, turn: "a"), S(2, 5, 0, 0, turn: "b"), S(3, 5, 0, 0, turn: "a")]);
    Check.Equal(3, rows.Count);
    Check.Equal(2, rows[0].Steps);
    Check.Equal(2L, rows[0].Read);
});

string Line(string json) => json.Replace('\'', '"') + "\n";

Test("cache: transcript reader counts main-loop requests once, from the request start", () =>
{
    var file = Path.Combine(TestContext.Root, "s.jsonl");
    File.WriteAllText(file,
        Line("{'type':'user','uuid':'u1','promptId':'p1','timestamp':'2026-10-08T10:00:00Z','cwd':'C:\\\\proj'}") +
        Line("{'type':'assistant','uuid':'a1','parentUuid':'u1','timestamp':'2026-10-08T10:00:20Z','message':{'id':'m1','model':'claude-sonnet-5-5','usage':{'input_tokens':2,'cache_read_input_tokens':100,'cache_creation_input_tokens':50,'output_tokens':7,'cache_creation':{'ephemeral_1h_input_tokens':50,'ephemeral_5m_input_tokens':0}}}}") +
        Line("{'type':'assistant','uuid':'a2','parentUuid':'a1','timestamp':'2026-10-08T10:00:21Z','message':{'id':'m1','model':'claude-sonnet-5-5','usage':{'input_tokens':2,'cache_read_input_tokens':100,'cache_creation_input_tokens':50,'output_tokens':7}}}") +
        Line("{'type':'assistant','isSidechain':true,'uuid':'s1','parentUuid':'u1','timestamp':'2026-10-08T10:00:22Z','message':{'id':'m2','model':'claude-haiku','usage':{'input_tokens':9,'cache_read_input_tokens':9}}}") +
        Line("{'type':'assistant','uuid':'x','parentUuid':'a2','timestamp':'2026-10-08T10:00:23Z','message':{'id':'m3','model':'<synthetic>','usage':{'input_tokens':0}}}") +
        "not json at all\n");
    var reader = new TranscriptReader(file);
    Check.True(reader.Refresh(), "changed");
    Check.Equal(1, reader.Samples.Count);
    var s = reader.Samples[0];
    Check.Equal(100L, s.Read);
    Check.Equal(50L, s.Write);
    Check.Equal("p1", s.TurnId);
    Check.Equal(DateTimeOffset.Parse("2026-10-08T10:00:00Z").ToUnixTimeMilliseconds(), s.StartedAt);
    Check.Equal(Ttl.OneHour, s.WrittenTtl);
    Check.Equal("C:\\proj", reader.Cwd);
    Check.True(!reader.Refresh(), "nothing new");
});

Test("cache: transcript reader reads appended lines and waits for a half-written one", () =>
{
    var file = Path.Combine(TestContext.Root, "s.jsonl");
    File.WriteAllText(file, Line("{'type':'user','uuid':'u1','promptId':'p1','timestamp':'2026-10-08T10:00:00Z'}"));
    var reader = new TranscriptReader(file);
    reader.Refresh();
    var half = Line("{'type':'assistant','uuid':'a1','parentUuid':'u1','timestamp':'2026-10-08T10:00:05Z','message':{'id':'m1','model':'m','usage':{'input_tokens':1,'cache_read_input_tokens':5}}}").TrimEnd('\n');
    File.AppendAllText(file, half[..30]);
    Check.True(!reader.Refresh(), "partial line ignored");
    File.AppendAllText(file, half[30..] + "\n");
    Check.True(reader.Refresh(), "completed line read");
    Check.Equal(1, reader.Samples.Count);
    File.WriteAllText(file, Line("{'type':'user','uuid':'u9','promptId':'p9','timestamp':'2026-10-08T11:00:00Z'}"));
    reader.Refresh();
    Check.Equal(0, reader.Samples.Count); // file was replaced: start over
});

Test("cache: analysis uses the written lifetime from usage records", () =>
{
    var samples = new[] { S(0, 100, 50, 0, written: Ttl.OneHour) };
    var view = CacheAnalyzer.Analyze(samples, new CacheOptions(), new CacheEnv(), null, Account.Other, 1000);
    Check.Equal(Ttl.OneHour, view.Ttl);
    Check.Equal("usage record", view.TtlSource);
    var forced = CacheAnalyzer.Analyze(samples, new CacheOptions(Ttl: Ttl.FiveMinutes), new CacheEnv(), null, Account.Other, 1000);
    Check.Equal(Ttl.FiveMinutes, forced.Ttl);
    Check.Equal(AdviceKind.Cold, CacheAnalyzer.Analyze([], new CacheOptions(), new CacheEnv(), null, Account.Other, 0).Advice.Kind);
});

Test("cache settings: edits only the cache keys, keeps the rest, backs up first", () =>
{
    var path = Path.Combine(TestContext.Root, "settings.json");
    File.WriteAllText(path, """{"model":"opus","env":{"FOO":"1","DISABLE_PROMPT_CACHING_HAIKU":"1"},"permissions":{"allow":["Bash(ls)"]}}""");
    var before = ClaudeCacheSettings.Read(path);
    Check.Equal("1", before.Env.DisableHaiku);

    var backup = ClaudeCacheSettings.Write(path, Path.Combine(TestContext.Root, "bk"),
        new CacheSettingsValues(new CacheEnv(Enable1h: "1"), "1h"), DateTime.UtcNow);
    Check.True(backup != null && File.Exists(backup), "backup made");

    var root = JsonFile.ReadOrNull(path)!;
    Check.Equal("opus", root["model"]!.GetValue<string>());
    Check.Equal("Bash(ls)", root["permissions"]!["allow"]![0]!.GetValue<string>());
    Check.Equal("1", root["env"]!["FOO"]!.GetValue<string>());
    Check.Equal("1", root["env"]!["ENABLE_PROMPT_CACHING_1H"]!.GetValue<string>());
    Check.True(root["env"]!["DISABLE_PROMPT_CACHING_HAIKU"] == null, "removed");
    Check.Equal("1h", root["promptCacheTtl"]!.GetValue<string>());

    ClaudeCacheSettings.Write(path, Path.Combine(TestContext.Root, "bk"), new CacheSettingsValues(new CacheEnv(), null), DateTime.UtcNow.AddSeconds(1));
    var cleared = JsonFile.ReadOrNull(path)!;
    Check.True(cleared["promptCacheTtl"] == null, "ttl removed");
    Check.Equal(1, cleared["env"]!.AsObject().Count); // only FOO remains
});

Test("cache settings: broken settings.json is never overwritten", () =>
{
    var path = Path.Combine(TestContext.Root, "settings.json");
    File.WriteAllText(path, "{ nope");
    Check.Throws<InvalidOperationException>(() =>
        ClaudeCacheSettings.Write(path, Path.Combine(TestContext.Root, "bk"), new CacheSettingsValues(new CacheEnv(), "1h"), DateTime.UtcNow));
    Check.Equal("{ nope", File.ReadAllText(path));
});

// ---- transfer: container ----------------------------------------------------
const int Chunk = 1 << 20;
const int FastIterations = 1000; // the real minimum is far higher; tests only exercise the format

byte[] Seal(byte[] plain, string pass)
{
    using var file = new MemoryStream();
    using (var enc = new EncryptingStream(file, pass, FastIterations, leaveOpen: true))
    {
        // Feed it in odd-sized pieces so chunk boundaries do not line up with write boundaries.
        for (var i = 0; i < plain.Length; i += 777_777) enc.Write(plain, i, Math.Min(777_777, plain.Length - i));
        enc.Complete();
    }
    return file.ToArray();
}

byte[] Unseal(byte[] sealedBytes, string pass)
{
    using var dec = new DecryptingStream(new MemoryStream(sealedBytes), pass);
    using var result = new MemoryStream();
    dec.CopyTo(result);
    return result.ToArray();
}

Test("transfer crypto: round trip at every chunk boundary", () =>
{
    foreach (var size in new[] { 0, 1, Chunk - 1, Chunk, Chunk + 1, 3 * Chunk + 5 })
    {
        var plain = RandomNumberGenerator.GetBytes(size);
        var back = Unseal(Seal(plain, "correct horse"), "correct horse");
        Check.True(plain.AsSpan().SequenceEqual(back), $"size {size} came back different");
    }
});

Test("transfer crypto: wrong passphrase and any tampering are rejected", () =>
{
    var plain = RandomNumberGenerator.GetBytes(2 * Chunk + 100);
    var sealedBytes = Seal(plain, "right-pass");
    Check.Throws<TransferException>(() => Unseal(sealedBytes, "wrong-pass"));

    var flipped = (byte[])sealedBytes.Clone();
    flipped[flipped.Length / 2] ^= 1;
    Check.Throws<TransferException>(() => Unseal(flipped, "right-pass"));

    var header = (byte[])sealedBytes.Clone();
    header[9] ^= 1; // iteration count: part of the authenticated header
    Check.Throws<TransferException>(() => Unseal(header, "right-pass"));

    // Cut exactly at a chunk boundary: still a valid-looking file, but the last chunk is not marked as last.
    var cut = sealedBytes[..(32 + Chunk + 16)];
    Check.Throws<TransferException>(() => Unseal(cut, "right-pass"));
    Check.Throws<TransferException>(() => Unseal(sealedBytes[..^3], "right-pass"));
    Check.Throws<TransferException>(() => Unseal("not a bundle at all, just some bytes"u8.ToArray(), "right-pass"));
});

Test("transfer crypto: seekable, so a ZIP can be read straight from it", () =>
{
    var plain = RandomNumberGenerator.GetBytes(2 * Chunk + 123);
    using var dec = new DecryptingStream(new MemoryStream(Seal(plain, "seek-pass")), "seek-pass");
    var buffer = new byte[50];
    foreach (var at in new long[] { 0, Chunk - 10, Chunk, plain.Length - 50, 5 })
    {
        dec.Position = at;
        dec.ReadExactly(buffer);
        Check.True(buffer.AsSpan().SequenceEqual(plain.AsSpan((int)at, 50)), $"bytes at {at}");
    }
    Check.Equal((long)plain.Length, dec.Length);
});

// ---- transfer: sign-in key --------------------------------------------------
Test("chromium key: write keeps the rest of Local State, read gives the raw key back", () =>
{
    var path = Path.Combine(TestContext.Root, "Local State");
    File.WriteAllText(path, """{"browser":{"enabled":true},"os_crypt":{"audit_enabled":true,"encrypted_key":"old"}}""");
    var key = RandomNumberGenerator.GetBytes(32);
    var prot = new FakeProtector("pc-a");
    ChromiumKey.Write(path, key, prot);
    Check.True(key.AsSpan().SequenceEqual(ChromiumKey.Read(path, prot)), "key round trip");
    var root = JsonFile.ReadOrNull(path)!;
    Check.Equal(true, root["browser"]!["enabled"]!.GetValue<bool>());
    Check.Equal(true, root["os_crypt"]!["audit_enabled"]!.GetValue<bool>());
    Check.True(ChromiumKey.Read(Path.Combine(TestContext.Root, "missing"), prot) == null, "missing file -> null");
    Check.Throws<TransferException>(() => ChromiumKey.Read(path, new FakeProtector("pc-b")));
});

Test("chromium key: a broken Local State is never overwritten", () =>
{
    var path = Path.Combine(TestContext.Root, "Local State");
    File.WriteAllText(path, "{ nope");
    Check.Throws<TransferException>(() => ChromiumKey.Write(path, new byte[32], new FakeProtector("x")));
    Check.Equal("{ nope", File.ReadAllText(path));
});

WindowsTest("chromium key: real Windows DPAPI protects and restores a key", () =>
{
    var prot = new DpapiKeyProtector();
    var key = RandomNumberGenerator.GetBytes(32);
    var protectedKey = prot.Protect(key);
    Check.True(!protectedKey.AsSpan().SequenceEqual(key), "protected form differs");
    Check.True(key.AsSpan().SequenceEqual(prot.Unprotect(protectedKey)), "DPAPI round trip");
});

// ---- transfer: what is worth moving -----------------------------------------
Test("transfer catalog: caches and machine state stay, chats/sign-ins/settings go", () =>
{
    TransferKind? Of(string root, string rel) => TransferCatalog.Classify(root, rel, false);
    Check.Equal(TransferKind.Chats, Of("instances", "claude-code-sessions/a/b/local_1.json"));
    Check.Equal(TransferKind.SignIn, Of("instances", "Network/Cookies"));
    Check.Equal(TransferKind.SignIn, Of("instances", "Local Storage/leveldb/000003.log"));
    Check.Equal(TransferKind.Settings, Of("instances", "claude_desktop_config.json"));
    foreach (var skipped in new[] { "Cache/x", "Code Cache/js/a", "GPUCache/x", "logs/main.log", "claude-code/2.1/claude.exe",
                 "Network/TransportSecurity", "lockfile", "config.json", "Local State", "ant-did", "window-state.json" })
        Check.True(Of("instances", skipped) == null, $"{skipped} must stay behind");

    Check.Equal(TransferKind.Chats, Of("claude-home", "projects/C--x/1.jsonl"));
    Check.Equal(TransferKind.SignIn, Of("claude-home", ".credentials.json"));
    Check.Equal(TransferKind.Settings, Of("claude-home", "settings.json"));
    Check.Equal(TransferKind.Settings, Of("claude-home", "skills/a/SKILL.md"));
    foreach (var skipped in new[] { "cache/x", "shell-snapshots/a.sh", "settings.json.bak.2026", "sessions/1.json", "x/.credentials.json.manager-tmp" })
        Check.True(Of("claude-home", skipped) == null, $"~/.claude/{skipped} must stay behind");
    Check.True(Of("claude-home", "sub/.credentials.json") == TransferKind.Settings, "only the top-level credentials are sign-in");
    Check.True(Of("nowhere", "x") == null, "unknown root");
});

// ---- transfer: PC to PC -----------------------------------------------------
const string Pass = "tasima-parolasi";

Test("transfer: everything moves to a new PC and the sign-in key is re-protected for it", () =>
{
    var key = RandomNumberGenerator.GetBytes(32);
    var a = TestContext.SeedPc("A", new FakeProtector("pc-a"), key, withSecondInstance: true);
    var b = TestContext.EnvAt("B");
    var protB = new FakeProtector("pc-b");

    var export = new TransferService(a, new FakeProtector("pc-a"))
        .Export(Path.Combine(TestContext.Root, "bundle.cmxfer"), Pass, TransferScope.All,
            new Dictionary<string, string> { ["2"] = "İş hesabı" });
    Check.True(export.Files > 10, "exported files");

    var result = new TransferService(b, protB).Import(export.Path, Pass, TransferScope.All);
    Check.Equal("İş hesabı", result.Labels["2"]);

    // sign-in: tokens, account id and the key, protected for PC B
    var mainB = new InstanceInfo("main", b.MainDataDir);
    var cfg = JsonFile.ReadOrNull(mainB.ConfigPath)!;
    Check.Equal("TOKEN-main", cfg["oauth:tokenCacheV2"]!.GetValue<string>());
    Check.Equal(TestContext.AccMain, cfg["lastKnownAccountUuid"]!.GetValue<string>());
    Check.True(key.AsSpan().SequenceEqual(ChromiumKey.Read(ChromiumKey.LocalStatePath(mainB), protB)), "key usable on PC B");
    Check.Equal("cookies-main", File.ReadAllText(Path.Combine(b.MainDataDir, "Network", "Cookies")));
    Check.Equal("ls-main", File.ReadAllText(Path.Combine(b.MainDataDir, "Local Storage", "leveldb", "000003.log")));

    // settings: portable keys arrive, machine-bound ones do not
    Check.Equal("dark", cfg["userThemeMode"]!.GetValue<string>());
    Check.True(!cfg.ContainsKey("quickWindowPosition"), "window position stays on PC A");
    Check.Equal("""{"mcpServers":{"x":{"command":"y"}}}""", File.ReadAllText(Path.Combine(b.MainDataDir, "claude_desktop_config.json")));
    Check.Equal("""{"model":"opus"}""", File.ReadAllText(Path.Combine(b.ClaudeHomeDir, "settings.json")));
    Check.Equal("""{"userID":"u1"}""", File.ReadAllText(b.ClaudeJsonPath));

    // chats of both copies and Claude Code's own logs
    foreach (var (id, dir) in new[] { ("main", b.MainDataDir), ("2", b.DataDirOf("2")) })
        Check.True(File.Exists(Path.Combine(dir, "claude-code-sessions", id == "main" ? TestContext.AccMain : TestContext.AccTwo, TestContext.OrgId, $"local_chat-{id}.json")), $"chat of {id}");
    Check.Equal("{\"turn\":1}", File.ReadAllText(Path.Combine(b.ClaudeHomeDir, "projects", "C--proj", "s1.jsonl")));
    Check.Equal("""{"token":"cli"}""", File.ReadAllText(Path.Combine(b.ClaudeHomeDir, ".credentials.json")));

    // second copy exists and has its own key
    var twoB = new InstanceInfo("2", b.DataDirOf("2"));
    Check.True(InstanceProbe.IsSignedIn(twoB), "copy 2 signed in");
    Check.True(ChromiumKey.Read(ChromiumKey.LocalStatePath(twoB), protB) is { Length: 32 }, "copy 2 key");

    // the tray switcher's saved sign-ins come along (they are encrypted with main's key, which PC B now has)
    Check.Equal("saved-profile", File.ReadAllText(Path.Combine(b.SwitcherDir, "accounts", "acc-1", "config.json")));
    Check.True(!Directory.Exists(Path.Combine(b.SwitcherDir, "backups")), "switcher backups stay");
    Check.True(!File.Exists(Path.Combine(b.SwitcherDir, "switcher.log")), "switcher log stays");

    // caches and the like never travel
    Check.True(!Directory.Exists(Path.Combine(b.MainDataDir, "Cache")), "cache stays");
    Check.True(!Directory.Exists(Path.Combine(b.ClaudeHomeDir, "cache")), "~/.claude/cache stays");
    Check.True(!File.Exists(Path.Combine(b.MainDataDir, "window-state.json")), "window state stays");
});

Test("transfer: an existing target is merged for chats, replaced for sign-in, and backed up", () =>
{
    var a = TestContext.SeedPc("A", new FakeProtector("pc-a"), RandomNumberGenerator.GetBytes(32), withSecondInstance: false);
    var b = TestContext.SeedPc("B", new FakeProtector("pc-b"), RandomNumberGenerator.GetBytes(32), withSecondInstance: false, tag: "B");
    var chatDir = Path.Combine(b.MainDataDir, "claude-code-sessions", TestContext.AccMain, TestContext.OrgId);
    File.WriteAllText(Path.Combine(chatDir, "local_chat-main.json"), """{"sessionId":"chat-main","lastActivityAt":9999999}"""); // newer than A's
    File.WriteAllText(Path.Combine(chatDir, "local_only-b.json"), """{"sessionId":"only-b","lastActivityAt":5}""");
    File.AppendAllText(Path.Combine(b.ClaudeHomeDir, "projects", "C--proj", "s1.jsonl"), "{\"turn\":2}\n{\"turn\":3}\n"); // longer than A's

    var export = new TransferService(a, new FakeProtector("pc-a")).Export(Path.Combine(TestContext.Root, "b.cmxfer"), Pass, TransferScope.All, new Dictionary<string, string>());
    var result = new TransferService(b, new FakeProtector("pc-b")).Import(export.Path, Pass, TransferScope.All);

    Check.Equal("9999999", JsonFile.ReadOrNull(Path.Combine(chatDir, "local_chat-main.json"))!["lastActivityAt"]!.ToString());
    Check.True(File.Exists(Path.Combine(chatDir, "local_only-b.json")), "B's own chat is not deleted");
    Check.True(File.ReadAllText(Path.Combine(b.ClaudeHomeDir, "projects", "C--proj", "s1.jsonl")).Contains("\"turn\":3"), "longer log kept");
    Check.True(result.FilesKept >= 2, "kept files counted");

    Check.Equal("TOKEN-main", JsonFile.ReadOrNull(Path.Combine(b.MainDataDir, "config.json"))!["oauth:tokenCacheV2"]!.GetValue<string>());
    Check.True(!File.Exists(Path.Combine(b.MainDataDir, "Local Storage", "leveldb", "from-B.log")), "old web storage replaced");
    var index = File.ReadAllText(Path.Combine(result.BackupDir, "index.txt"));
    Check.True(index.Contains("config.json") && index.Contains("Local Storage"), "replaced files are backed up and listed");
});

Test("transfer: only the chosen kinds are carried", () =>
{
    var a = TestContext.SeedPc("A", new FakeProtector("pc-a"), RandomNumberGenerator.GetBytes(32), withSecondInstance: false);
    var b = TestContext.EnvAt("B");
    var chatsOnly = new TransferScope(SignIns: false, Chats: true, Settings: false);
    var export = new TransferService(a, new FakeProtector("pc-a")).Export(Path.Combine(TestContext.Root, "c.cmxfer"), Pass, chatsOnly, new Dictionary<string, string>());
    Check.True(export.Manifest.Instances.All(i => i.Key == null), "no key in a chats-only bundle");

    new TransferService(b, new FakeProtector("pc-b")).Import(export.Path, Pass, TransferScope.All);
    Check.True(!File.Exists(Path.Combine(b.MainDataDir, "config.json")), "no config in a chats-only bundle");
    Check.True(!File.Exists(Path.Combine(b.ClaudeHomeDir, "settings.json")), "no settings");
    Check.True(!File.Exists(Path.Combine(b.MainDataDir, "Network", "Cookies")), "no cookies");
    Check.True(File.Exists(Path.Combine(b.ClaudeHomeDir, "projects", "C--proj", "s1.jsonl")), "chats came");

    // The importer can narrow a full bundle further, too.
    var full = new TransferService(a, new FakeProtector("pc-a")).Export(Path.Combine(TestContext.Root, "full.cmxfer"), Pass, TransferScope.All, new Dictionary<string, string>());
    var b2 = TestContext.EnvAt("B2");
    new TransferService(b2, new FakeProtector("pc-b")).Import(full.Path, Pass, new TransferScope(false, false, true));
    var cfg = JsonFile.ReadOrNull(Path.Combine(b2.MainDataDir, "config.json"))!;
    Check.Equal("dark", cfg["userThemeMode"]!.GetValue<string>());
    Check.True(!cfg.ContainsKey("oauth:tokenCacheV2"), "tokens not taken when only settings are chosen");
    Check.True(!File.Exists(Path.Combine(b2.MainDataDir, "Local State")), "key not written either");
});

Test("transfer: wrong passphrase changes nothing; short passphrase is refused", () =>
{
    var a = TestContext.SeedPc("A", new FakeProtector("pc-a"), RandomNumberGenerator.GetBytes(32), withSecondInstance: false);
    var b = TestContext.EnvAt("B");
    var service = new TransferService(a, new FakeProtector("pc-a"));
    Check.Throws<TransferException>(() => service.Export(Path.Combine(TestContext.Root, "x.cmxfer"), "short", TransferScope.All, new Dictionary<string, string>()));
    Check.True(!File.Exists(Path.Combine(TestContext.Root, "x.cmxfer")), "no file left behind");

    var export = service.Export(Path.Combine(TestContext.Root, "y.cmxfer"), Pass, TransferScope.All, new Dictionary<string, string>());
    Check.Throws<TransferException>(() => new TransferService(b, new FakeProtector("pc-b")).Import(export.Path, "another-pass", TransferScope.All));
    Check.Throws<TransferException>(() => service.Inspect(export.Path, "another-pass"));
    Check.True(!Directory.Exists(b.MainDataDir), "PC B untouched");
    Check.Equal(1, service.Inspect(export.Path, Pass).Instances.Count);
});

Test("transfer: a running Claude blocks sign-in export and any import", () =>
{
    var a = TestContext.SeedPc("A", new FakeProtector("pc-a"), RandomNumberGenerator.GetBytes(32), withSecondInstance: false);
    var service = new TransferService(a, new FakeProtector("pc-a"));
    using (new FileStream(Path.Combine(a.MainDataDir, "lockfile"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
    {
        Check.Throws<TransferException>(() => service.Export(Path.Combine(TestContext.Root, "r.cmxfer"), Pass, TransferScope.All, new Dictionary<string, string>()));
        var chats = service.Export(Path.Combine(TestContext.Root, "r2.cmxfer"), Pass, new TransferScope(false, true, true), new Dictionary<string, string>());
        Check.True(File.Exists(chats.Path), "chats and settings can be exported while Claude runs");
        Check.Throws<TransferException>(() => service.Import(chats.Path, Pass, TransferScope.All));
    }
});

Test("transfer: entries that try to leave their folder are refused", () =>
{
    var b = TestContext.EnvAt("B");
    var bundle = Path.Combine(TestContext.Root, "evil.cmxfer");
    var manifest = new TransferManifest { CreatedAt = DateTimeOffset.Now, Chats = true, Settings = true };
    manifest.Instances.Add(new ManifestInstance { Id = "main" });
    using (var file = File.Create(bundle))
    using (var enc = new EncryptingStream(file, Pass, FastIterations, leaveOpen: true))
    {
        using (var zip = new ZipArchive(enc, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(text);
            }
            Add("manifest.json", System.Text.Json.JsonSerializer.Serialize(manifest));
            Add("claude-home/../escaped.txt", "x");
            Add("claude-home/projects/../../escaped2.txt", "x");
            Add("instances/main/claude-code-sessions/..\\..\\escaped3.txt", "x");
            Add("instances/..\\x/claude-code-sessions/a.json", "x");
            Add("instances/main/claude-code-sessions/C:evil.txt", "x");
            Add("claude-home/projects/NUL", "x");
            Add("claude-home/projects/fine.jsonl", "ok");
        }
        enc.Complete();
    }
    var result = new TransferService(b, new FakeProtector("pc-b")).Import(bundle, Pass, TransferScope.All);
    Check.Equal(1, result.FilesWritten);
    Check.True(result.Warnings.Count >= 6, "each bad entry reported");
    Check.True(File.Exists(Path.Combine(b.ClaudeHomeDir, "projects", "fine.jsonl")), "the safe entry went through");
    foreach (var dir in new[] { TestContext.Root, Path.Combine(TestContext.Root, "B"), b.Home, b.ClaudeHomeDir })
        Check.True(!Directory.EnumerateFiles(dir, "escaped*").Any(), $"nothing escaped into {dir}");
});

Test("transfer: a failure halfway puts every file back", () =>
{
    var a = TestContext.SeedPc("A", new FakeProtector("pc-a"), RandomNumberGenerator.GetBytes(32), withSecondInstance: false);
    var b = TestContext.SeedPc("B", new FakeProtector("pc-b"), RandomNumberGenerator.GetBytes(32), withSecondInstance: false, tag: "B");
    var export = new TransferService(a, new FakeProtector("pc-a")).Export(Path.Combine(TestContext.Root, "f.cmxfer"), Pass, TransferScope.All, new Dictionary<string, string>());

    // B's config.json cannot be merged, which only comes up after the files were already written.
    File.WriteAllText(Path.Combine(b.MainDataDir, "config.json"), "{ broken");
    var cookiesBefore = File.ReadAllText(Path.Combine(b.MainDataDir, "Network", "Cookies"));
    var settingsBefore = File.ReadAllText(Path.Combine(b.ClaudeHomeDir, "settings.json"));
    var localStateBefore = File.ReadAllText(Path.Combine(b.MainDataDir, "Local State"));

    var error = Check.Throws<TransferException>(() => new TransferService(b, new FakeProtector("pc-b")).Import(export.Path, Pass, TransferScope.All));
    Check.True(error.Message.Contains("rolled back"), "tells the user it was rolled back");
    Check.Equal(cookiesBefore, File.ReadAllText(Path.Combine(b.MainDataDir, "Network", "Cookies")));
    Check.Equal(settingsBefore, File.ReadAllText(Path.Combine(b.ClaudeHomeDir, "settings.json")));
    Check.Equal(localStateBefore, File.ReadAllText(Path.Combine(b.MainDataDir, "Local State")));
    Check.Equal("{ broken", File.ReadAllText(Path.Combine(b.MainDataDir, "config.json")));
    Check.True(File.Exists(Path.Combine(b.MainDataDir, "Local Storage", "leveldb", "from-B.log")), "old web storage restored");
    Check.True(!File.Exists(Path.Combine(b.ClaudeHomeDir, "projects", "C--proj", "s1.jsonl.manager-tmp")), "no temp leftovers");
    Check.True(!File.Exists(Path.Combine(b.MainDataDir, "claude-code-sessions", TestContext.AccMain, TestContext.OrgId, "local_chat-A-only.json")), "imported chat removed again");
});

// ---- API key instances (Claude Code CLI) --------------------------------------
ApiSettings OpenRouter(string key = "sk-or-v1-abcdef123456") => new(ApiProfile.OpenRouterBaseUrl, key, ["z-ai/glm-5.3", "anthropic/claude-haiku-5.5"]);

Test("api: key is stored sealed and loads back", () =>
{
    var env = TestContext.Env();
    var inst = InstanceCatalog.Create(env, "api1");
    Check.True(!inst.IsApi, "plain instance before saving");
    var protector = new FakeProtector("me");
    ApiProfile.Save(inst, OpenRouter(), protector);

    Check.True(inst.IsApi, "API instance after saving");
    Check.True(!File.ReadAllText(ApiProfile.PathOf(inst)).Contains("sk-or-v1-abcdef123456"), "key not in plain text on disk");
    var loaded = ApiProfile.Load(inst, protector)!;
    Check.Equal("sk-or-v1-abcdef123456", loaded.ApiKey);
    Check.Equal("https://openrouter.ai/api", loaded.BaseUrl);
    Check.Equal("z-ai/glm-5.3,anthropic/claude-haiku-5.5", string.Join(",", loaded.Models));
});

Test("api: a key sealed for another Windows user loads as empty", () =>
{
    var env = TestContext.Env();
    var inst = InstanceCatalog.Create(env, "api2");
    ApiProfile.Save(inst, OpenRouter(), new FakeProtector("me"));
    var other = new FakeProtector("someone-else");
    Check.Equal("", ApiProfile.Load(inst, other)!.ApiKey);
});

Test("api: saving replaces the old plain-text Desktop config", () =>
{
    var env = TestContext.Env();
    var inst = InstanceCatalog.Create(env, "api3");
    var lib = Path.Combine(inst.DataDir, "local", "Claude-3p", "configLibrary");
    var id = Guid.NewGuid().ToString();
    Directory.CreateDirectory(lib);
    File.WriteAllText(Path.Combine(lib, "_meta.json"), $$"""{"appliedId":"{{id}}","entries":[{"id":"{{id}}","name":"x"}]}""");
    File.WriteAllText(Path.Combine(lib, id + ".json"), """{"inferenceGatewayBaseUrl":"https://openrouter.ai/api","inferenceGatewayApiKey":"sk-or-old-key-123","inferenceModels":[{"name":"anthropic/claude-sonnet-5.5"}]}""");

    Check.True(inst.IsApi, "legacy instance is recognised");
    var legacy = ApiProfile.Load(inst, new FakeProtector("me"))!;
    Check.Equal("sk-or-old-key-123", legacy.ApiKey);

    ApiProfile.Save(inst, legacy with { Models = ["z-ai/glm-5.3"] }, new FakeProtector("me"));
    Check.True(!Directory.Exists(lib), "plain-text key removed");
    Check.Equal("z-ai/glm-5.3", ApiProfile.Load(inst, new FakeProtector("me"))!.Models[0]);
});

Test("api: refuses the main instance and bad input", () =>
{
    var env = TestContext.Env();
    Directory.CreateDirectory(env.MainDataDir);
    var main = new InstanceInfo(ClaudeEnvironment.MainId, env.MainDataDir);
    Check.Throws<InvalidOperationException>(() => ApiProfile.Save(main, OpenRouter(), new FakeProtector("me")));
    Check.True(!File.Exists(ApiProfile.PathOf(main)), "main untouched");

    Check.True(ApiProfile.Validate(OpenRouter() with { BaseUrl = "http://openrouter.ai/api" }) != null, "http rejected");
    Check.True(ApiProfile.Validate(OpenRouter("short")) != null, "short key rejected");
    Check.True(ApiProfile.Validate(OpenRouter("sk-or v1 spaced")) != null, "key with spaces rejected");
    Check.True(ApiProfile.Validate(OpenRouter() with { Models = [] }) != null, "no models rejected");
    Check.True(ApiProfile.Validate(OpenRouter() with { WorkDir = Path.Combine(TestContext.Root, "nope") }) != null, "missing folder rejected");
    Check.True(ApiProfile.Validate(OpenRouter()) == null, "valid settings accepted");
});

Test("api: CLI environment follows OpenRouter's Claude Code setup", () =>
{
    var env = TestContext.Env();
    var inst = InstanceCatalog.Create(env, "api4");
    var vars = ApiProfile.CliEnvironment(inst, OpenRouter());
    Check.Equal("https://openrouter.ai/api", vars["ANTHROPIC_BASE_URL"]);
    Check.Equal("sk-or-v1-abcdef123456", vars["ANTHROPIC_AUTH_TOKEN"]);
    Check.Equal("", vars["ANTHROPIC_API_KEY"]);
    Check.Equal("z-ai/glm-5.3", vars["ANTHROPIC_MODEL"]);
    Check.Equal("z-ai/glm-5.3", vars["ANTHROPIC_DEFAULT_OPUS_MODEL"]);
    Check.Equal("anthropic/claude-haiku-5.5", vars["ANTHROPIC_DEFAULT_HAIKU_MODEL"]);
    Check.Equal(ApiProfile.ClaudeConfigDirOf(inst), vars["CLAUDE_CONFIG_DIR"]);
    Check.True(vars["CLAUDE_CONFIG_DIR"].StartsWith(inst.DataDir), "Claude Code config stays inside the instance");
});

WindowsTest("api: start info runs the CLI in a kept-open console with the environment", () =>
{
    var env = TestContext.Env();
    var inst = InstanceCatalog.Create(env, "api5");
    var work = Path.Combine(TestContext.Root, "proj");
    Directory.CreateDirectory(work);
    var info = CliLauncher.BuildStartInfo(@"C:\Users\x\.local\bin\claude.exe", inst, OpenRouter() with { WorkDir = work, ContextTokens = 1_000_000 }, env.Home);
    Check.Equal("powershell.exe", info.FileName);
    Check.Equal("-NoLogo -NoExit -Command \"& 'C:\\Users\\x\\.local\\bin\\claude.exe'\"", info.Arguments);
    Check.Equal("1000000", info.Environment["CLAUDE_CODE_AUTO_COMPACT_WINDOW"]);
    var skip = CliLauncher.BuildStartInfo(@"C:\Users\x\.local\bin\claude.exe", inst, OpenRouter() with { SkipPermissions = true }, env.Home);
    Check.True(skip.Arguments!.EndsWith("claude.exe' --dangerously-skip-permissions\""), "skip-permissions flag added");
    Check.Equal(work, info.WorkingDirectory);
    Check.Equal("z-ai/glm-5.3", info.Environment["ANTHROPIC_MODEL"]);
    Check.True(Directory.Exists(ApiProfile.ClaudeConfigDirOf(inst)), "config dir created");
});

Test("api: finds the CLI on PATH, then in ~/.local/bin (Windows names)", () =>
{
    var home = Path.Combine(TestContext.Root, "home");
    var bin = Path.Combine(TestContext.Root, "bin");
    Directory.CreateDirectory(bin);
    Check.True(CliLauncher.FindCli(home, bin, null, HostOs.Windows) == null, "nothing installed");
    var local = Path.Combine(home, ".local", "bin", "claude.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(local)!);
    File.WriteAllText(local, "");
    Check.Equal(local, CliLauncher.FindCli(home, bin, null, HostOs.Windows));
    var onPath = Path.Combine(bin, "claude.exe");
    File.WriteAllText(onPath, "");
    Check.Equal(onPath, CliLauncher.FindCli(home, "bad\0dir" + Path.PathSeparator + bin, null, HostOs.Windows));
});

Test("api: mask never shows the whole key", () =>
{
    Check.Equal("sk-or-…7890", ApiProfile.Mask("sk-or-v1-abcdef1234567890"));
    Check.True(!ApiProfile.Mask("12345678").Contains('5'), "short keys fully hidden");
});

Test("api: sign-in links and sync skip API instances", () =>
{
    var env = TestContext.Env();
    var api = InstanceCatalog.Create(env, "api6");
    ApiProfile.Save(api, OpenRouter(), new FakeProtector("me"));
    InstanceCatalog.Create(env, "plain");
    var instances = InstanceCatalog.List(env);

    var state = new ManagerState { PendingLoginId = "api6", PendingSince = DateTimeOffset.UtcNow };
    Check.Equal("plain", ProtocolRouter.ResolveTarget(state, instances, DateTimeOffset.UtcNow)!.Id);
    Check.Equal(1, new SyncEngine(env, new StateStore(env.StatePath)).Run().Instances);
});

// ---- macOS and Linux ---------------------------------------------------------
Test("platform: Claude Desktop's data folder and launcher on macOS and Linux", () =>
{
    Check.Equal(Path.Combine("/Users/me", "Library", "Application Support", "Claude"), ClaudeEnvironment.MacDataDir("/Users/me"));
    Check.Equal(Path.Combine("/home/me", ".config", "Claude"), ClaudeEnvironment.LinuxDataDir("/home/me", null));
    Check.Equal(Path.Combine("/cfg", "Claude"), ClaudeEnvironment.LinuxDataDir("/home/me", "/cfg"));

    var home = Path.Combine(TestContext.Root, "mac-home");
    var system = Path.Combine(TestContext.Root, "system-apps");
    Check.True(ClaudeEnvironment.FindMacApp(home, system) == null, "no Claude.app yet");
    var userApp = Path.Combine(home, "Applications", "Claude.app", "Contents", "MacOS", "Claude");
    Directory.CreateDirectory(Path.GetDirectoryName(userApp)!);
    File.WriteAllText(userApp, "");
    Check.Equal(userApp, ClaudeEnvironment.FindMacApp(home, system));
    var systemApp = Path.Combine(system, "Claude.app", "Contents", "MacOS", "Claude");
    Directory.CreateDirectory(Path.GetDirectoryName(systemApp)!);
    File.WriteAllText(systemApp, "");
    Check.Equal(systemApp, ClaudeEnvironment.FindMacApp(home, system));

    var bin = Path.Combine(TestContext.Root, "bin");
    Directory.CreateDirectory(bin);
    Check.True(ClaudeEnvironment.FindLinuxDesktop(bin) == null, "Linux has no official Claude Desktop");
    var launcher = Path.Combine(bin, "claude-desktop");
    File.WriteAllText(launcher, "");
    Check.Equal(launcher, ClaudeEnvironment.FindLinuxDesktop("bad\0dir" + Path.PathSeparator + bin));
});

Test("probe: the SingletonLock link target names the owning process", () =>
{
    Check.Equal<int?>(4242, InstanceProbe.SingletonOwnerPid("my-laptop-4242"));
    Check.Equal<int?>(7, InstanceProbe.SingletonOwnerPid("host-7"));
    foreach (var bad in new[] { null, "", "nopid", "host-0", "host-abc", "-5" })
        Check.True(InstanceProbe.SingletonOwnerPid(bad) == null, $"'{bad}' has no pid");
});

UnixTest("probe: a SingletonLock of a live process means running, a stale one does not", () =>
{
    var env = TestContext.Env();
    var inst = InstanceCatalog.Create(env, "p");
    Check.True(!InstanceProbe.IsRunning(inst), "no lock -> not running");
    File.CreateSymbolicLink(inst.SingletonLockPath, $"{Environment.MachineName}-{Environment.ProcessId}");
    Check.True(InstanceProbe.IsRunning(inst), "lock of a live process -> running");
    File.Delete(inst.SingletonLockPath);
    File.CreateSymbolicLink(inst.SingletonLockPath, $"{Environment.MachineName}-{int.MaxValue - 1}");
    Check.True(!InstanceProbe.IsRunning(inst), "lock left by a crashed process -> not running");
});

Test("closer: Linux folder names are case sensitive, macOS and Windows are not", () =>
{
    var inst = new InstanceInfo("2", "/home/me/ClaudeInstances/2");
    const string exe = "/opt/Claude/claude";
    Check.True(InstanceCloser.IsRootOf(exe + " --user-data-dir=/home/me/ClaudeInstances/2", inst, HostOs.Linux), "same folder");
    Check.True(InstanceCloser.IsRootOf(exe + " --user-data-dir=/home/me/ClaudeInstances/2/", inst, HostOs.Linux), "trailing slash");
    Check.True(!InstanceCloser.IsRootOf(exe + " --user-data-dir=/home/me/claudeinstances/2", inst, HostOs.Linux), "other case is another folder on Linux");
    Check.True(InstanceCloser.IsRootOf(exe + " --user-data-dir=/home/me/claudeinstances/2", inst, HostOs.MacOS), "same folder on macOS");
    Check.True(!InstanceCloser.IsRootOf(exe + " --user-data-dir=/home/me/ClaudeInstances/22", inst, HostOs.Linux), "22 is not 2");
});

Test("closer: /proc and ps command lines are read the same way, spaces included", () =>
{
    var argv = UnixProcesses.FromNulSeparated(Encoding.UTF8.GetBytes("/opt/Claude/claude\0--user-data-dir=/home/my user/ClaudeInstances/2\0--no-sandbox\0"));
    var line = UnixProcesses.FromArgv(argv);
    Check.Equal("/opt/Claude/claude --user-data-dir=\"/home/my user/ClaudeInstances/2\" --no-sandbox", line);
    Check.Equal(line, UnixProcesses.FromPsArgs("/opt/Claude/claude --user-data-dir=/home/my user/ClaudeInstances/2 --no-sandbox"));
    Check.Equal("/opt/Claude/claude --user-data-dir=/a/b --x", UnixProcesses.FromPsArgs("/opt/Claude/claude --user-data-dir=/a/b --x"));
    Check.Equal("claude --resume", UnixProcesses.FromPsArgs("claude --resume"));

    var inst = new InstanceInfo("2", "/home/my user/ClaudeInstances/2");
    Check.True(InstanceCloser.IsRootOf(line, inst, HostOs.Linux), "root of the instance");
    Check.True(!InstanceCloser.IsRootOf(line + " --type=gpu-process", inst, HostOs.Linux), "a helper is not the root");
});

Test("closer: Claude Desktop is told apart from its helpers and from the Claude Code CLI", () =>
{
    var mac = new ClaudeEnvironment("m", "i", "/Applications/Claude.app/Contents/MacOS/Claude");
    Check.True(InstanceCloser.IsDesktopProcess(new UnixProcess(1, null, "/Applications/Claude.app/Contents/MacOS/Claude"), mac, HostOs.MacOS), "app");
    Check.True(InstanceCloser.IsDesktopProcess(new UnixProcess(1, null, "/Applications/Claude.app/Contents/MacOS/Claude --user-data-dir=/x"), mac, HostOs.MacOS), "app with arguments");
    Check.True(!InstanceCloser.IsDesktopProcess(new UnixProcess(2, null, "/Applications/Claude.app/Contents/Frameworks/Claude Helper.app/Contents/MacOS/Claude Helper --type=gpu"), mac, HostOs.MacOS), "helper");
    Check.True(!InstanceCloser.IsDesktopProcess(new UnixProcess(3, null, "/Users/me/.local/bin/claude --resume"), mac, HostOs.MacOS), "CLI");

    var linux = new ClaudeEnvironment("m", "i", "/opt/Claude/claude");
    Check.True(InstanceCloser.IsDesktopProcess(new UnixProcess(4, "/opt/Claude/claude", "claude --x"), linux, HostOs.Linux), "kernel exe path");
    Check.True(!InstanceCloser.IsDesktopProcess(new UnixProcess(5, "/home/me/.local/bin/claude", "claude"), linux, HostOs.Linux), "CLI");
    Check.True(!InstanceCloser.IsDesktopProcess(new UnixProcess(6, "/opt/Claude/claude", "claude"), new ClaudeEnvironment("m", "i", null), HostOs.Linux), "no known Desktop");
});

Test("launcher: macOS and Linux start Claude detached, Windows starts the exe directly", () =>
{
    var win = InstanceLauncher.BuildStartInfo(@"C:\c\claude.exe", [@"--user-data-dir=C:\x y"], HostOs.Windows);
    Check.Equal(@"C:\c\claude.exe", win.FileName);
    Check.Equal(@"--user-data-dir=C:\x y", win.ArgumentList.Single());

    var mac = InstanceLauncher.BuildStartInfo("/Applications/Claude.app/Contents/MacOS/Claude", ["--user-data-dir=/Users/me/My Instances/2", "claude://x"], HostOs.MacOS);
    Check.Equal("/bin/sh", mac.FileName);
    Check.Equal("-c", mac.ArgumentList[0]);
    Check.True(mac.ArgumentList[1].StartsWith("nohup "), "detached from this program and its terminal");
    Check.Equal("/Applications/Claude.app/Contents/MacOS/Claude", mac.ArgumentList[2]);
    Check.Equal("--user-data-dir=/Users/me/My Instances/2", mac.ArgumentList[3]);
    Check.Equal("claude://x", mac.ArgumentList[4]);
});

Test("api: finds the CLI on macOS and Linux", () =>
{
    var home = Path.Combine(TestContext.Root, "home");
    var bin = Path.Combine(TestContext.Root, "bin");
    Directory.CreateDirectory(bin);
    var local = Path.Combine(home, ".local", "bin", "claude");
    Directory.CreateDirectory(Path.GetDirectoryName(local)!);
    File.WriteAllText(local, "");
    Check.Equal(local, CliLauncher.FindCli(home, bin, null, HostOs.Linux));
    var onPath = Path.Combine(bin, "claude");
    File.WriteAllText(onPath, "");
    Check.Equal(onPath, CliLauncher.FindCli(home, "bad\0dir" + Path.PathSeparator + bin, null, HostOs.MacOS));
});

Test("api: terminal windows for macOS and Linux run this program, not the CLI with the key", () =>
{
    var inst = new InstanceInfo("api1", "/x/api1");
    var command = CliLauncher.SessionCommand(["/opt/cm/ClaudeMulti"], inst);
    Check.Equal("/opt/cm/ClaudeMulti --run-cli api1", string.Join(' ', command));

    var mac = CliLauncher.BuildTerminalStartInfo(HostOs.MacOS, ["/Applications/Claude Multi/ClaudeMulti", "--run-cli", "api1"], null, null);
    Check.Equal("osascript", mac.FileName);
    var script = mac.ArgumentList[3];
    Check.True(script.Contains("do script") && script.Contains("'/Applications/Claude Multi/ClaudeMulti' '--run-cli' 'api1'"), script);
    Check.Equal("a\\\"b\\\\c", HostPlatform.AppleScriptEscape("a\"b\\c"));
    Check.Equal("'it'\\''s'", HostPlatform.ShellQuote("it's"));

    var bin = Path.Combine(TestContext.Root, "terms");
    Directory.CreateDirectory(bin);
    Check.True(CliLauncher.FindLinuxTerminal(bin, null) == null, "no terminal installed");
    Check.Throws<InvalidOperationException>(() => CliLauncher.BuildTerminalStartInfo(HostOs.Linux, command, bin, null));
    File.WriteAllText(Path.Combine(bin, "xterm"), "");
    File.WriteAllText(Path.Combine(bin, "gnome-terminal"), "");
    Check.Equal("gnome-terminal", CliLauncher.FindLinuxTerminal(bin, null)!.Value.Program);
    Check.Equal("xterm", CliLauncher.FindLinuxTerminal(bin, "xterm")!.Value.Program);
    var linux = CliLauncher.BuildTerminalStartInfo(HostOs.Linux, command, bin, null);
    Check.Equal("gnome-terminal", linux.FileName);
    Check.Equal("-- /opt/cm/ClaudeMulti --run-cli api1", string.Join(' ', linux.ArgumentList));
});

Test("api: the console session gets the key through its environment, never its arguments", () =>
{
    var env = TestContext.Env();
    var inst = InstanceCatalog.Create(env, "api8");
    var info = CliLauncher.BuildCliInfo("/home/me/.local/bin/claude", inst, OpenRouter() with { SkipPermissions = true }, env.Home);
    Check.Equal("/home/me/.local/bin/claude", info.FileName);
    Check.Equal("--dangerously-skip-permissions", info.ArgumentList.Single());
    Check.Equal("sk-or-v1-abcdef123456", info.Environment["ANTHROPIC_AUTH_TOKEN"]);
    Check.True(!info.ArgumentList.Any(a => a.Contains("sk-or")), "no key in arguments");
    Check.True(Directory.Exists(ApiProfile.ClaudeConfigDirOf(inst)), "config dir created");
});

Test("key protector: a key file seals secrets and only that key opens them", () =>
{
    var path = Path.Combine(TestContext.Root, "a", "protector.key");
    var a = new FileKeyProtector(path);
    var secret = Encoding.UTF8.GetBytes("sk-or-v1-abcdef123456");
    var sealedSecret = a.Protect(secret);
    Check.True(sealedSecret.AsSpan().IndexOf(secret.AsSpan()) < 0, "not readable in the sealed form");
    Check.True(secret.AsSpan().SequenceEqual(a.Unprotect(sealedSecret)), "round trip");
    Check.True(secret.AsSpan().SequenceEqual(new FileKeyProtector(path).Unprotect(sealedSecret)), "a new instance reads the same key file");
    Check.True(!a.SupportsChromiumKey, "cannot hold Claude Desktop's key");

    var other = new FileKeyProtector(Path.Combine(TestContext.Root, "b", "protector.key"));
    Check.Throws<TransferException>(() => other.Unprotect(sealedSecret));          // no key file there
    other.Protect([1]);
    Check.Throws<TransferException>(() => other.Unprotect(sealedSecret));          // another user's key
    var tampered = (byte[])sealedSecret.Clone();
    tampered[^1] ^= 1;
    Check.Throws<TransferException>(() => a.Unprotect(tampered));
    Check.Throws<TransferException>(() => a.Unprotect([1, 2, 3]));
});

UnixTest("key protector: the key file is private to the user", () =>
{
    var path = Path.Combine(TestContext.Root, "keys", "protector.key");
    new FileKeyProtector(path).Protect([1]);
    var mode = File.GetUnixFileMode(path);
    Check.True((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) == 0, $"mode {mode}");
});

Test("api: a key saved with the file protector reloads, is not written in clear, and comes back empty elsewhere", () =>
{
    var env = TestContext.Env();
    var inst = InstanceCatalog.Create(env, "api9");
    var mine = new FileKeyProtector(Path.Combine(TestContext.Root, "mine", "protector.key"));
    ApiProfile.Save(inst, OpenRouter(), mine);
    Check.True(!File.ReadAllText(ApiProfile.PathOf(inst)).Contains("abcdef123456"), "key not in clear text");
    Check.Equal("sk-or-v1-abcdef123456", ApiProfile.Load(inst, mine)!.ApiKey);
    Check.Equal("", ApiProfile.Load(inst, new FileKeyProtector(Path.Combine(TestContext.Root, "theirs", "protector.key")))!.ApiKey);
});

Test("state: the lock file guards concurrent updates the way the mutex does on Windows", () =>
{
    var path = Path.Combine(TestContext.Root, "counter.txt");
    File.WriteAllText(path, "0");
    Parallel.For(0, 12, _ =>
    {
        using var gate = StateLock.Acquire(path, TimeSpan.FromSeconds(10), useFile: true);
        File.WriteAllText(path, (int.Parse(File.ReadAllText(path)) + 1).ToString());
    });
    Check.Equal("12", File.ReadAllText(path));
});

Test("linux router: mimeapps.list and .desktop formats", () =>
{
    const string list = "[Added Associations]\nx-scheme-handler/claude=other.desktop;\n\n[Default Applications]\ntext/html=a.desktop;\nx-scheme-handler/claude=claude-desktop.desktop;claude-multi-handler.desktop;\n";
    Check.Equal("claude-desktop.desktop", LinuxProtocolRegistry.ParseDefault(list, LinuxProtocolRegistry.MimeType));
    Check.True(LinuxProtocolRegistry.ParseDefault("[Added Associations]\nx-scheme-handler/claude=other.desktop;\n", LinuxProtocolRegistry.MimeType) == null, "only the default section counts");

    var cleared = LinuxProtocolRegistry.RemoveDefault(list, LinuxProtocolRegistry.MimeType, "claude-desktop.desktop");
    Check.True(!cleared.Contains("claude-desktop.desktop"), "our default entry removed");
    Check.True(cleared.Contains("text/html=a.desktop") && cleared.Contains("x-scheme-handler/claude=other.desktop;"), "other lines kept");
    Check.Equal(list, LinuxProtocolRegistry.RemoveDefault(list, LinuxProtocolRegistry.MimeType, "someone-else.desktop"));

    var entry = LinuxProtocolRegistry.BuildDesktopEntry("\"/opt/cm/ClaudeMulti\" --uri \"%1\"");
    Check.True(entry.Contains("Exec=\"/opt/cm/ClaudeMulti\" --uri %u\n"), entry);
    Check.True(entry.Contains("MimeType=x-scheme-handler/claude;"), "declares the scheme");
    Check.Equal("\"/opt/cm/ClaudeMulti\" --uri \"%1\"", LinuxProtocolRegistry.CommandFromExec("\"/opt/cm/ClaudeMulti\" --uri %u"));
    Check.True(LinuxProtocolRegistry.CommandFromExec(null) == null, "no Exec");
});

Test("linux router: the handler file and mimeapps.list round-trip through the router's command", () =>
{
    var env = TestContext.Env();
    var data = Path.Combine(TestContext.Root, "data");
    var config = Path.Combine(TestContext.Root, "config");
    Directory.CreateDirectory(config);
    var mimeApps = Path.Combine(config, "mimeapps.list");
    var registry = new LinuxProtocolRegistry(data, config, Path.Combine(TestContext.Root, "home"));
    var router = new ProtocolRouter(registry, new StateStore(env.StatePath), "/opt/cm/ClaudeMulti");

    router.RegisterHandler();
    var desktopFile = Path.Combine(data, "applications", LinuxProtocolRegistry.DesktopFileName);
    Check.True(File.Exists(desktopFile), "handler file written");

    File.WriteAllText(mimeApps, $"[Default Applications]\nx-scheme-handler/claude={LinuxProtocolRegistry.DesktopFileName};\n");
    Check.Equal(router.HookCommand, registry.GetCommand());
    Check.True(router.IsHooked && router.IsRoutedHere, "hooked");

    File.WriteAllText(mimeApps, "[Default Applications]\nx-scheme-handler/claude=claude-desktop.desktop;\n");
    Check.Equal("desktop:claude-desktop.desktop", registry.GetCommand());
    Check.True(!router.IsHooked, "Claude took the link back");

    File.WriteAllText(mimeApps, $"[Default Applications]\nx-scheme-handler/claude={LinuxProtocolRegistry.DesktopFileName};\n");
    registry.ClearCommand();
    Check.True(!File.ReadAllText(mimeApps).Contains(LinuxProtocolRegistry.DesktopFileName), "our default removed when there was none before");
});

Test("macOS router: refuses to take over claude: links instead of pretending", () =>
{
    var env = TestContext.Env();
    var store = new StateStore(env.StatePath);
    var router = new ProtocolRouter(new UnsupportedProtocolRegistry("not here"), store, "/opt/cm/ClaudeMulti");
    Check.True(!router.IsSupported && router.UnsupportedReason == "not here", "reported as unsupported");
    Check.Throws<IOException>(() => router.Enable());
    Check.True(!store.Load().HookEnabled, "nothing was switched on");
});

Test("transfer: sign-ins move between Windows PCs only; chats and settings move everywhere", () =>
{
    var a = TestContext.SeedPc("A", new FakeProtector("pc-a"), RandomNumberGenerator.GetBytes(32), withSecondInstance: false);
    var b = TestContext.EnvAt("B");
    var unix = new TransferService(b, new FileKeyProtector(Path.Combine(TestContext.Root, "keyB", "protector.key")));
    Check.True(!unix.SupportsSignIns, "key files cannot carry Claude Desktop's key");
    Check.Throws<TransferException>(() => unix.Export(Path.Combine(TestContext.Root, "x.cmxfer"), Pass, TransferScope.All, new Dictionary<string, string>()));

    var bundle = new TransferService(a, new FakeProtector("pc-a")).Export(Path.Combine(TestContext.Root, "w.cmxfer"), Pass, TransferScope.All, new Dictionary<string, string>());
    Check.Equal(TransferService.OsName(HostPlatform.Current), bundle.Manifest.Os);
    Check.True(!bundle.Warnings.Any(w => w.Contains("made on")), "nothing to warn about on the same system");

    Check.Throws<TransferException>(() => unix.Import(bundle.Path, Pass, new TransferScope(true, false, false)));
    var result = unix.Import(bundle.Path, Pass, TransferScope.All);
    Check.True(result.Warnings.Any(w => w.Contains("Windows only")), "says why sign-ins stayed behind");
    Check.True(!File.Exists(Path.Combine(b.MainDataDir, "Local State")), "no foreign encryption key written");
    Check.True(!File.Exists(Path.Combine(b.ClaudeHomeDir, ".credentials.json")), "no sign-in file copied");
    Check.True(JsonFile.ReadOrNull(Path.Combine(b.MainDataDir, "config.json"))?.ContainsKey("oauth:tokenCacheV2") != true, "no token imported");
    Check.Equal("""{"model":"opus"}""", File.ReadAllText(Path.Combine(b.ClaudeHomeDir, "settings.json")));
    Check.True(File.Exists(Path.Combine(b.MainDataDir, "claude-code-sessions", TestContext.AccMain, TestContext.OrgId, "local_chat-main.json")), "chat arrived");
});

Console.WriteLine($"\n{total - failures}/{total} passed");
return failures;

/// <summary>Stands in for DPAPI: a key protected by one fake user cannot be opened by another.</summary>
internal sealed class FakeProtector(string user) : IKeyProtector
{
    private byte[] Head => Encoding.ASCII.GetBytes(user + ":");

    public byte[] Protect(byte[] data) => [.. Head, .. data.Select(b => (byte)(b ^ 0x5A))];

    public byte[] Unprotect(byte[] data)
    {
        var head = Head;
        if (!data.AsSpan().StartsWith(head)) throw new TransferException("wrong Windows user");
        return data[head.Length..].Select(b => (byte)(b ^ 0x5A)).ToArray();
    }
}

internal sealed class FakeRegistry : IProtocolRegistry
{
    public string? Command { get; set; }
    public string? GetCommand() => Command;
    public void SetCommand(string command) => Command = command;
    public string? ProgId { get; set; }
    public bool HandlerRegistered { get; private set; }
    public string? ResolveProgId() => ProgId;
    public void RegisterHandler(string command) => HandlerRegistered = true;
}

internal static class TestContext
{
    public static string Root { get; set; } = "";

    public const string AccMain = "aaaaaaaa-1111-0000-0000-000000000001";
    public const string AccTwo = "bbbbbbbb-1111-0000-0000-000000000002";
    public const string OrgId = "oooooooo-1111-0000-0000-000000000003";

    public static ClaudeEnvironment Env() => EnvAt("");

    /// <summary>A throw-away "PC": its own Claude folder, instances folder and home, all under the test root.</summary>
    public static ClaudeEnvironment EnvAt(string pc)
    {
        var root = pc.Length == 0 ? Root : Path.Combine(Root, pc);
        return new(Path.Combine(root, "main"), Path.Combine(root, "instances"), null, Path.Combine(root, "home"));
    }

    /// <summary>Fills a PC with a signed-in main Claude (and optionally a copy), Claude Code data and some cache that must not travel.</summary>
    public static ClaudeEnvironment SeedPc(string pc, IKeyProtector protector, byte[] key, bool withSecondInstance, string tag = "")
    {
        var env = EnvAt(pc);
        SeedInstance(new InstanceInfo("main", env.MainDataDir), AccMain, "main", pc, tag, protector, key);
        if (withSecondInstance)
        {
            var two = InstanceCatalog.Create(env, "2");
            SeedInstance(two, AccTwo, "2", pc, tag, protector, RandomNumberGenerator.GetBytes(32));
        }

        var claude = env.ClaudeHomeDir;
        Directory.CreateDirectory(Path.Combine(claude, "projects", "C--proj"));
        Directory.CreateDirectory(Path.Combine(claude, "cache"));
        File.WriteAllText(Path.Combine(claude, "settings.json"), tag.Length == 0 ? """{"model":"opus"}""" : """{"model":"sonnet"}""");
        File.WriteAllText(Path.Combine(claude, "projects", "C--proj", "s1.jsonl"), "{\"turn\":1}");
        File.WriteAllText(Path.Combine(claude, ".credentials.json"), tag.Length == 0 ? """{"token":"cli"}""" : """{"token":"cli-b"}""");
        File.WriteAllText(Path.Combine(claude, "cache", "junk.bin"), "junk");
        File.WriteAllText(env.ClaudeJsonPath, tag.Length == 0 ? """{"userID":"u1"}""" : """{"userID":"u2"}""");

        // The tray switcher's saved sign-ins travel; its backups and log do not.
        Directory.CreateDirectory(Path.Combine(env.SwitcherDir, "accounts", "acc-1"));
        Directory.CreateDirectory(Path.Combine(env.SwitcherDir, "backups", "20260101-000000"));
        File.WriteAllText(Path.Combine(env.SwitcherDir, "state.json"), tag + "{\"accounts\":[]}");
        File.WriteAllText(Path.Combine(env.SwitcherDir, "accounts", "acc-1", "config.json"), tag + "saved-profile");
        File.WriteAllText(Path.Combine(env.SwitcherDir, "backups", "20260101-000000", "config.json"), "old");
        File.WriteAllText(Path.Combine(env.SwitcherDir, "switcher.log"), "log");
        return env;
    }

    private static void SeedInstance(InstanceInfo inst, string account, string id, string pc, string tag, IKeyProtector protector, byte[] key)
    {
        var dir = inst.DataDir;
        void Put(string rel, string text)
        {
            var path = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Directory.CreateDirectory(dir);
        WriteConfig(inst, new JsonObject
        {
            ["oauth:tokenCacheV2"] = tag + "TOKEN-" + id,
            ["oauth:tokenCache"] = tag + "OLD-" + id,
            ["lastKnownAccountUuid"] = account,
            ["userThemeMode"] = tag.Length == 0 ? "dark" : "light",
            ["locale"] = "tr-TR",
            ["quickWindowPosition"] = "position-on-" + pc,
        });
        var encryptedKey = Convert.ToBase64String([.. "DPAPI"u8.ToArray(), .. protector.Protect(key)]);
        Put("Local State", "{\"browser\":{\"x\":1},\"os_crypt\":{\"encrypted_key\":\"" + encryptedKey + "\"}}");
        Put("Network/Cookies", tag + "cookies-" + id);
        Put("Network/TransportSecurity", "junk");
        Put("Local Storage/leveldb/000003.log", tag + "ls-" + id);
        Put($"Local Storage/leveldb/from-{pc}.log", "x");
        Put("Cache/blob", "cache");
        Put("window-state.json", "window");
        Put("claude_desktop_config.json", tag.Length == 0 ? """{"mcpServers":{"x":{"command":"y"}}}""" : """{"mcpServers":{}}""");
        AddChat(inst, account, OrgId, "chat-" + id, 1000);
        AddChat(inst, account, OrgId, $"chat-{pc}-only", 1000);
    }

    public static (InstanceInfo Main, InstanceInfo Two) TwoInstances()
    {
        var env = Env();
        Directory.CreateDirectory(Path.Combine(env.MainDataDir, "claude-code-sessions"));
        var two = new InstanceInfo("two", Path.Combine(Root, "two"));
        Directory.CreateDirectory(Path.Combine(two.DataDir, "claude-code-sessions"));
        return (new InstanceInfo("main", env.MainDataDir), two);
    }

    public static void WriteConfig(InstanceInfo inst, JsonObject values, bool merge = false)
    {
        var obj = merge ? JsonFile.ReadOrNull(inst.ConfigPath) ?? new JsonObject() : new JsonObject();
        foreach (var (k, v) in values) obj[k] = v?.DeepClone();
        JsonFile.Write(inst.ConfigPath, obj);
    }

    public static string ChatPath(InstanceInfo inst, string acc, string org, string id) =>
        Path.Combine(inst.SessionsDir, acc, org, $"local_{id}.json");

    public static void AddChat(InstanceInfo inst, string acc, string org, string id, long activity)
    {
        var path = ChatPath(inst, acc, org, id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"{{\"sessionId\":\"{id}\",\"lastActivityAt\":{activity}}}");
    }
}

internal static class Check
{
    public static void True(bool condition, string message = "condition was false")
    {
        if (!condition) throw new Exception(message);
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"expected <{expected}> but got <{actual}>");
    }

    public static TException Throws<TException>(Action body) where TException : Exception
    {
        try { body(); }
        catch (TException e) { return e; }
        throw new Exception($"expected {typeof(TException).Name}");
    }
}
