using System.Text;
using System.Text.Json;

namespace ClaudeMulti.Core.Cache;

/// <summary>
/// Reads the usage numbers of a Claude Code conversation from its transcript (.jsonl),
/// incrementally. Only main-loop requests count (subagent "sidechain" requests are skipped),
/// and a response written as several records is counted once.
/// </summary>
public sealed class TranscriptReader(string path)
{
    private readonly List<Sample> samples = [];
    private readonly Dictionary<string, long> recordTimes = new();
    private long offset;
    private string turn = "start";
    private string? lastMessageId;

    public string Path { get; } = path;
    public string? Cwd { get; private set; }
    public IReadOnlyList<Sample> Samples => samples;

    /// <summary>Reads what was appended since the last call. Returns true when samples changed.</summary>
    public bool Refresh()
    {
        FileStream fs;
        try
        {
            fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        using (fs)
        {
            if (fs.Length < offset) Reset();
            if (fs.Length == offset) return false;

            fs.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[fs.Length - offset];
            var read = fs.Read(buffer, 0, buffer.Length);
            var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
            if (lastNewline < 0) return false; // a line is still being written

            var before = samples.Count;
            var text = Encoding.UTF8.GetString(buffer, 0, lastNewline + 1);
            var changed = false;
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                changed |= ReadLine(line);
            offset += lastNewline + 1;
            return changed || samples.Count != before;
        }
    }

    private void Reset()
    {
        samples.Clear();
        recordTimes.Clear();
        offset = 0;
        turn = "start";
        lastMessageId = null;
    }

    private bool ReadLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var time = ReadTime(root);
            var uuid = Text(root, "uuid");
            if (uuid != null && time != null) recordTimes[uuid] = time.Value;
            Cwd ??= Text(root, "cwd");

            switch (Text(root, "type"))
            {
                case "user":
                    turn = Text(root, "promptId") ?? uuid ?? turn;
                    return false;
                case "assistant" when !(root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True):
                    return ReadAssistant(root, time);
                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false; // a damaged line must not stop the rest
        }
    }

    private bool ReadAssistant(JsonElement root, long? time)
    {
        if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("usage", out var usage)) return false;
        var model = Text(msg, "model") ?? "";
        if (model.StartsWith('<')) return false; // "<synthetic>" placeholders carry no usage

        var messageId = Text(msg, "id");
        if (messageId != null && messageId == lastMessageId) return false;
        lastMessageId = messageId;

        // The request began when its parent record (prompt or tool result) was written.
        var parent = Text(root, "parentUuid");
        var started = parent != null && recordTimes.TryGetValue(parent, out var p) ? p : time;
        if (started == null) return false;

        long read = Number(usage, "cache_read_input_tokens");
        long write = Number(usage, "cache_creation_input_tokens");
        long fresh = Number(usage, "input_tokens");
        if (read + write + fresh == 0) return false;

        samples.Add(new Sample(turn, model, started.Value, read, write, fresh, Number(usage, "output_tokens"), WrittenTtl(usage)));
        return true;
    }

    private static Ttl? WrittenTtl(JsonElement usage)
    {
        if (!usage.TryGetProperty("cache_creation", out var detail) || detail.ValueKind != JsonValueKind.Object) return null;
        if (Number(detail, "ephemeral_1h_input_tokens") > 0) return Ttl.OneHour;
        return Number(detail, "ephemeral_5m_input_tokens") > 0 ? Ttl.FiveMinutes : null;
    }

    private static long? ReadTime(JsonElement root)
    {
        var text = Text(root, "timestamp");
        return text != null && DateTimeOffset.TryParse(text, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToUnixTimeMilliseconds()
            : null;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static long Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.TryGetInt64(out var n) ? n : 0;
}

public sealed record TranscriptInfo(string Path, string Project, DateTime LastWriteUtc);

public static class TranscriptFinder
{
    /// <summary>The most recently written conversations under ~/.claude/projects, newest first.</summary>
    public static IReadOnlyList<TranscriptInfo> Recent(string projectsDir, int count)
    {
        if (!Directory.Exists(projectsDir)) return [];
        return Directory.EnumerateFiles(projectsDir, "*.jsonl", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .Where(f => !f.Directory!.Name.Equals("subagents", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(count)
            .Select(f => new TranscriptInfo(f.FullName, f.Directory!.Name, f.LastWriteTimeUtc))
            .ToList();
    }
}
