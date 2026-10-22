using System.Text;
using System.Text.Json.Nodes;
using ClaudeMulti.Core.Transfer;

namespace ClaudeMulti.Core;

/// <summary>What an API-key instance talks to: an Anthropic-compatible gateway such as OpenRouter.</summary>
public sealed record ApiSettings(
    string BaseUrl,
    string ApiKey,
    IReadOnlyList<string> Models,
    string WorkDir = "",
    int ContextTokens = 0,
    bool SkipPermissions = false);

/// <summary>
/// An instance that runs Claude Code (CLI) on an API key instead of Claude Desktop on a claude.ai account.
/// Stored in <c>&lt;instance&gt;\api-profile.json</c>; the key is DPAPI-protected for the current Windows user.
/// </summary>
public static class ApiProfile
{
    public const string OpenRouterBaseUrl = "https://openrouter.ai/api";

    /// <summary>A starting list for OpenRouter; the first one is the default model.</summary>
    public static readonly IReadOnlyList<string> DefaultModels = ["z-ai/glm-5.3", "anthropic/claude-sonnet-5.5"];

    public const int MinContextTokens = 10_000;
    public const int MaxContextTokens = 2_000_000;

    private const string FileName = "api-profile.json";

    public static string PathOf(InstanceInfo instance) => Path.Combine(instance.DataDir, FileName);

    /// <summary>Claude Code's own config folder for this instance, so it never picks up a claude.ai login from ~/.claude.</summary>
    public static string ClaudeConfigDirOf(InstanceInfo instance) => Path.Combine(instance.DataDir, "claude-code");

    // Earlier builds ran API instances as Claude Desktop in third-party mode, with the key in plain text here.
    private static string LegacyLibraryDir(InstanceInfo instance) => Path.Combine(instance.DataDir, "local", "Claude-3p", "configLibrary");

    public static bool IsConfigured(InstanceInfo instance) =>
        File.Exists(PathOf(instance)) || File.Exists(Path.Combine(LegacyLibraryDir(instance), "_meta.json"));

    /// <summary>Returns an error message, or null when the settings can be saved.</summary>
    public static string? Validate(ApiSettings settings)
    {
        if (!Uri.TryCreate(settings.BaseUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return "The address must be a valid URL starting with https://.";
        var key = settings.ApiKey.Trim();
        if (key.Length < 8 || key.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            return "The API key is invalid.";
        if (settings.Models.Count == 0)
            return "En az bir model girin.";
        if (settings.Models.Any(m => m.Length == 0 || m.Length > 200 || m.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '"')))
            return "Model names cannot contain spaces.";
        if (settings.WorkDir.Length > 0 && !Directory.Exists(settings.WorkDir))
            return "Working folder not found.";
        if (settings.ContextTokens != 0 && settings.ContextTokens is < MinContextTokens or > MaxContextTokens)
            return $"Context must be between {MinContextTokens:N0} and {MaxContextTokens:N0} tokens (empty = default).";
        return null;
    }

    public static void Save(InstanceInfo instance, ApiSettings settings, IKeyProtector protector)
    {
        if (instance.IsMain) throw new InvalidOperationException("The main Claude cannot be used with an API key; create a new instance.");
        if (Validate(settings) is { } error) throw new ArgumentException(error, nameof(settings));

        var sealedKey = protector.Protect(Encoding.UTF8.GetBytes(settings.ApiKey.Trim()));
        JsonFile.Write(PathOf(instance), new JsonObject
        {
            ["baseUrl"] = settings.BaseUrl.Trim().TrimEnd('/'),
            ["apiKeyProtected"] = Convert.ToBase64String(sealedKey),
            ["models"] = new JsonArray(settings.Models.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
            ["workDir"] = settings.WorkDir,
            ["contextTokens"] = settings.ContextTokens,
            ["skipPermissions"] = settings.SkipPermissions,
        });

        // Drop the old plain-text copy of the key, if an earlier build wrote one.
        if (Directory.Exists(LegacyLibraryDir(instance))) Directory.Delete(LegacyLibraryDir(instance), recursive: true);
    }

    /// <summary>The saved settings, or null when the instance is not an API instance. A key that cannot be unsealed comes back empty.</summary>
    public static ApiSettings? Load(InstanceInfo instance, IKeyProtector protector)
    {
        var obj = JsonFile.ReadOrNull(PathOf(instance));
        if (obj == null) return LoadLegacy(instance);

        var key = "";
        try
        {
            if (obj["apiKeyProtected"]?.ToString() is { Length: > 0 } sealedKey)
                key = Encoding.UTF8.GetString(protector.Unprotect(Convert.FromBase64String(sealedKey)));
        }
        catch (Exception e) when (e is FormatException or TransferException)
        {
            key = ""; // another Windows user or PC: the user has to enter it again
        }
        var models = (obj["models"] as JsonArray ?? []).Select(m => m?.ToString() ?? "").Where(m => m.Length > 0).ToList();
        var context = int.TryParse(obj["contextTokens"]?.ToString(), out var tokens) ? tokens : 0;
        var skip = bool.TryParse(obj["skipPermissions"]?.ToString(), out var skipValue) && skipValue;
        return new ApiSettings(obj["baseUrl"]?.ToString() ?? "", key, models, obj["workDir"]?.ToString() ?? "", context, skip);
    }

    private static ApiSettings? LoadLegacy(InstanceInfo instance)
    {
        var id = JsonFile.ReadOrNull(Path.Combine(LegacyLibraryDir(instance), "_meta.json"))?["appliedId"]?.ToString();
        if (id == null || !Guid.TryParse(id, out _)) return null;
        var entry = JsonFile.ReadOrNull(Path.Combine(LegacyLibraryDir(instance), id + ".json"));
        if (entry == null) return null;
        var models = (entry["inferenceModels"] as JsonArray ?? [])
            .Select(m => m is JsonObject o ? o["name"]?.ToString() : m?.ToString())
            .Where(m => !string.IsNullOrEmpty(m))
            .Select(m => m!)
            .ToList();
        return new ApiSettings(entry["inferenceGatewayBaseUrl"]?.ToString() ?? "", entry["inferenceGatewayApiKey"]?.ToString() ?? "", models);
    }

    /// <summary>
    /// Environment for Claude Code on a gateway (OpenRouter's documented setup): the key goes in ANTHROPIC_AUTH_TOKEN,
    /// ANTHROPIC_API_KEY must be empty, and every model role points at one of the listed models.
    /// </summary>
    public static IReadOnlyDictionary<string, string> CliEnvironment(InstanceInfo instance, ApiSettings settings)
    {
        var main = settings.Models[0];
        string Tier(string tier) => settings.Models.FirstOrDefault(m => m.Contains(tier, StringComparison.OrdinalIgnoreCase)) ?? main;
        var vars = new Dictionary<string, string>
        {
            ["ANTHROPIC_BASE_URL"] = settings.BaseUrl.Trim().TrimEnd('/'),
            ["ANTHROPIC_AUTH_TOKEN"] = settings.ApiKey.Trim(),
            ["ANTHROPIC_API_KEY"] = "",
            ["ANTHROPIC_MODEL"] = main,
            ["ANTHROPIC_DEFAULT_OPUS_MODEL"] = Tier("opus"),
            ["ANTHROPIC_DEFAULT_SONNET_MODEL"] = Tier("sonnet"),
            ["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = Tier("haiku"),
            ["CLAUDE_CODE_SUBAGENT_MODEL"] = main,
            ["CLAUDE_CONFIG_DIR"] = ClaudeConfigDirOf(instance),
        };
        // Claude Code assumes a 200k window for unknown models; this tells it the real size so auto-compact fires correctly.
        if (settings.ContextTokens > 0)
            vars["CLAUDE_CODE_AUTO_COMPACT_WINDOW"] = settings.ContextTokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return vars;
    }

    /// <summary>"sk-or-…3f9a": enough to tell keys apart, never the whole key.</summary>
    public static string Mask(string key)
    {
        key = key.Trim();
        return key.Length <= 12 ? new string('•', key.Length) : $"{key[..6]}…{key[^4..]}";
    }
}
