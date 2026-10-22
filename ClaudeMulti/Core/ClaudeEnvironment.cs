namespace ClaudeMulti.Core;

/// <summary>Where Claude Desktop and the extra instances live on this machine.</summary>
public sealed record ClaudeEnvironment(string MainDataDir, string InstancesRoot, string? ClaudeExe, string? UserHome = null, string? StoreAppId = null)
{
    public const string MainId = "main";

    /// <summary>Points at a Claude Desktop executable on any system, for installs the app cannot find by itself.</summary>
    public const string ExeOverrideVariable = "CLAUDEMULTI_CLAUDE_EXE";

    /// <summary>True when a Claude Desktop copy can be started, either as a plain exe or as an MSIX package.</summary>
    public bool CanLaunch => ClaudeExe != null || StoreAppId != null;

    /// <summary>The user's home folder, where Claude Code keeps its own data.</summary>
    public string Home => UserHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string ClaudeHomeDir => Path.Combine(Home, ".claude");
    public string ClaudeJsonPath => Path.Combine(Home, ".claude.json");
    public string SwitcherDir => Path.Combine(Home, ".claude-account-switcher");

    /// <summary>Copies of files an import replaced. Separate from <see cref="BackupsDir"/>, which settings sync prunes.</summary>
    public string ImportBackupsDir => Path.Combine(InstancesRoot, "import-backups");

    public string StatePath => Path.Combine(InstancesRoot, "manager-state.json");
    public string SyncRecordPath => Path.Combine(InstancesRoot, "sync-records.json");
    public string BackupsDir => Path.Combine(InstancesRoot, "backups");
    public string LogPath => Path.Combine(InstancesRoot, "manager.log");

    public string DataDirOf(string id) => id == MainId ? MainDataDir : Path.Combine(InstancesRoot, id);

    public static ClaudeEnvironment Detect()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Test hook: run against a throw-away folder and never start a real Claude.
        var testRoot = Environment.GetEnvironmentVariable("CLAUDEMULTI_TEST_ROOT");
        if (!string.IsNullOrEmpty(testRoot))
            return new ClaudeEnvironment(Path.Combine(testRoot, "main"), Path.Combine(testRoot, "instances"), null, Path.Combine(testRoot, "home"));

        var instances = Path.Combine(profile, "ClaudeInstances");
        var overridden = Environment.GetEnvironmentVariable(ExeOverrideVariable);
        var exeOverride = !string.IsNullOrWhiteSpace(overridden) && File.Exists(overridden) ? overridden : null;

        switch (HostPlatform.Current)
        {
            case HostOs.MacOS:
                return new ClaudeEnvironment(MacDataDir(profile), instances, exeOverride ?? FindMacApp(profile));
            case HostOs.Linux:
                var path = Environment.GetEnvironmentVariable("PATH");
                var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                return new ClaudeEnvironment(LinuxDataDir(profile, config), instances, exeOverride ?? FindLinuxDesktop(path));
            default:
                return DetectWindows(profile, instances, exeOverride);
        }
    }

    // ---- Windows ----

    private static ClaudeEnvironment DetectWindows(string profile, string instances, string? exeOverride)
    {
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // Store (MSIX) installs keep their data in a package folder; the newest config.json wins.
        var candidates = new List<string>();
        var packages = Path.Combine(local, "Packages");
        if (Directory.Exists(packages))
        {
            foreach (var pkg in Directory.GetDirectories(packages, "Claude_*"))
                candidates.Add(Path.Combine(pkg, "LocalCache", "Roaming", "Claude"));
        }
        candidates.Add(Path.Combine(roaming, "Claude"));

        var main = candidates[^1];
        var newest = DateTime.MinValue;
        foreach (var dir in candidates)
        {
            var cfg = Path.Combine(dir, "config.json");
            if (!File.Exists(cfg)) continue;
            var t = File.GetLastWriteTimeUtc(cfg);
            if (t > newest) { main = dir; newest = t; }
        }
        var exe = exeOverride ?? FindClaudeExe(local);
        return new ClaudeEnvironment(main, instances, exe, null, exe == null ? StoreApp.FindAppId() : null);
    }

    /// <summary>The newest directly installed claude.exe (Squirrel layout), or null.</summary>
    public static string? FindClaudeExe(string localAppData)
    {
        var root = Path.Combine(localAppData, "AnthropicClaude");
        if (Directory.Exists(root))
        {
            var best = Directory.GetDirectories(root, "app-*")
                .Select(d => (Dir: d, Ver: ParseVersion(Path.GetFileName(d))))
                .Where(x => x.Ver != null && File.Exists(Path.Combine(x.Dir, "claude.exe")))
                .OrderByDescending(x => x.Ver)
                .FirstOrDefault();
            if (best.Dir != null) return Path.Combine(best.Dir, "claude.exe");
        }
        var programs = Path.Combine(localAppData, "Programs", "Claude", "Claude.exe");
        return File.Exists(programs) ? programs : null;
    }

    private static Version? ParseVersion(string folderName) =>
        Version.TryParse(folderName.StartsWith("app-") ? folderName[4..] : folderName, out var v) ? v : null;

    // ---- macOS ----

    public static string MacDataDir(string home) => Path.Combine(home, "Library", "Application Support", "Claude");

    /// <summary>The executable inside Claude.app. Running it directly (not through <c>open</c>) is what starts a second copy.</summary>
    public static string? FindMacApp(string home, string systemApplications = "/Applications")
    {
        foreach (var apps in new[] { systemApplications, Path.Combine(home, "Applications") })
        {
            var exe = Path.Combine(apps, "Claude.app", "Contents", "MacOS", "Claude");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    // ---- Linux ----

    /// <summary>Anthropic ships no Claude Desktop for Linux; this is where community builds keep their data.</summary>
    public static string LinuxDataDir(string home, string? xdgConfigHome) =>
        Path.Combine(string.IsNullOrWhiteSpace(xdgConfigHome) ? Path.Combine(home, ".config") : xdgConfigHome, "Claude");

    /// <summary>A community build's launcher on PATH (<c>claude-desktop</c>), or null. Nothing else is guessed.</summary>
    public static string? FindLinuxDesktop(string? pathVariable)
    {
        foreach (var dir in (pathVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, "claude-desktop");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { } // malformed PATH entry
        }
        return null;
    }
}
