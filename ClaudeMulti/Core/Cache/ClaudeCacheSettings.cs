using System.Text.Json.Nodes;

namespace ClaudeMulti.Core.Cache;

/// <summary>What the user wants in Claude Code's settings.json for prompt caching.</summary>
public sealed record CacheSettingsValues(CacheEnv Env, string? PromptCacheTtl);

/// <summary>
/// Reads and edits the prompt-cache switches in the user-level Claude Code settings
/// (~/.claude/settings.json: the "env" block and "promptCacheTtl"). Everything else in the file
/// is left as it is, and the previous file is backed up before every change.
/// </summary>
public static class ClaudeCacheSettings
{
    public const string TtlSettingKey = "promptCacheTtl";

    public static readonly IReadOnlyList<string> EnvNames =
    [
        "DISABLE_PROMPT_CACHING", "DISABLE_PROMPT_CACHING_HAIKU", "DISABLE_PROMPT_CACHING_SONNET",
        "DISABLE_PROMPT_CACHING_OPUS", "ENABLE_PROMPT_CACHING_1H", "FORCE_PROMPT_CACHING_5M",
        "CLAUDE_CODE_PROMPT_CACHE_TTL",
    ];

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    public static CacheSettingsValues Read(string path)
    {
        var root = JsonFile.ReadOrNull(path);
        var env = root?["env"] as JsonObject;
        string? Var(string name) => env?[name]?.ToString();
        return new CacheSettingsValues(
            new CacheEnv(
                Enable1h: Var("ENABLE_PROMPT_CACHING_1H"),
                Force5m: Var("FORCE_PROMPT_CACHING_5M"),
                TtlVar: Var("CLAUDE_CODE_PROMPT_CACHE_TTL"),
                DisableAll: Var("DISABLE_PROMPT_CACHING"),
                DisableHaiku: Var("DISABLE_PROMPT_CACHING_HAIKU"),
                DisableSonnet: Var("DISABLE_PROMPT_CACHING_SONNET"),
                DisableOpus: Var("DISABLE_PROMPT_CACHING_OPUS")),
            root?[TtlSettingKey]?.ToString());
    }

    /// <summary>Writes the given values. A null value removes the entry. Returns the backup path, if one was made.</summary>
    public static string? Write(string path, string backupsDir, CacheSettingsValues values, DateTime nowUtc)
    {
        var (state, existing) = JsonFile.Read(path);
        if (state == DocState.Invalid)
            throw new InvalidOperationException("Could not read settings.json (broken JSON); not changed.");

        string? backup = null;
        if (state == DocState.Ok)
        {
            Directory.CreateDirectory(backupsDir);
            backup = Path.Combine(backupsDir, $"claude-settings-{nowUtc:yyyyMMdd-HHmmss}.json");
            File.Copy(path, backup, overwrite: true);
        }

        var root = existing ?? new JsonObject();
        var env = root["env"] as JsonObject ?? new JsonObject();
        var wanted = new Dictionary<string, string?>
        {
            ["DISABLE_PROMPT_CACHING"] = values.Env.DisableAll,
            ["DISABLE_PROMPT_CACHING_HAIKU"] = values.Env.DisableHaiku,
            ["DISABLE_PROMPT_CACHING_SONNET"] = values.Env.DisableSonnet,
            ["DISABLE_PROMPT_CACHING_OPUS"] = values.Env.DisableOpus,
            ["ENABLE_PROMPT_CACHING_1H"] = values.Env.Enable1h,
            ["FORCE_PROMPT_CACHING_5M"] = values.Env.Force5m,
            ["CLAUDE_CODE_PROMPT_CACHE_TTL"] = values.Env.TtlVar,
        };
        foreach (var (name, value) in wanted)
        {
            if (value == null) env.Remove(name); else env[name] = value;
        }
        if (env.Count > 0) root["env"] = env; else root.Remove("env");

        if (values.PromptCacheTtl == null) root.Remove(TtlSettingKey); else root[TtlSettingKey] = values.PromptCacheTtl;

        JsonFile.Write(path, root, tabs: false);
        return backup;
    }
}
