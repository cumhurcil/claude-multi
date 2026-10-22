using System.Globalization;

namespace ClaudeMulti.Core.Cache;

/// <summary>
/// The prompt-cache arithmetic of the "prompt-cache-control" mod (claude-code-templates,
/// observability/prompt-cache-control): hit ratio, lifetime resolution and inference,
/// miss causes, countdown, advice and formatting. Times are milliseconds since the epoch.
/// </summary>
public static class CacheMath
{
    private const long FiveMinutesMs = 300_000;
    private const long OneHourMs = 3_600_000;
    private const long SlackMs = 10_000;
    private static readonly int[] CountdownMarks = [10, 3, 2, 1];

    public static long TtlMs(Ttl ttl) => ttl == Ttl.OneHour ? OneHourMs : FiveMinutesMs;

    public static string TtlLabel(Ttl ttl) => ttl == Ttl.OneHour ? "1 hour" : "5 min";

    public static long PromptTokens(Sample s) => s.Read + s.Write + s.Fresh;

    public static double HitRatio(Sample s) => Ratio(s.Read, PromptTokens(s));

    public static double RowRatio(TurnRow r) => Ratio(r.Read, r.Read + r.Write + r.Fresh);

    private static double Ratio(long part, long total) => total == 0 ? 0 : (double)part / total;

    // ---- lifetime ----------------------------------------------------------------

    public static Ttl? ParseTtl(string? value) => value switch
    {
        "5m" => Ttl.FiveMinutes,
        "1h" => Ttl.OneHour,
        _ => null,
    };

    public static bool IsOn(string? value) =>
        value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>First rule that yields a value wins: option, force-5m, env TTL, setting, enable-1h, account.</summary>
    public static TtlDecision DecideTtl(Ttl? option, CacheEnv env, string? setting, Account account)
    {
        if (option is { } o) return new(o, "setting");
        if (IsOn(env.Force5m)) return new(Ttl.FiveMinutes, "FORCE_PROMPT_CACHING_5M");
        if (ParseTtl(env.TtlVar) is { } fromEnv) return new(fromEnv, "CLAUDE_CODE_PROMPT_CACHE_TTL");
        if (ParseTtl(setting) is { } fromSetting) return new(fromSetting, "promptCacheTtl");
        if (IsOn(env.Enable1h)) return new(Ttl.OneHour, "ENABLE_PROMPT_CACHING_1H");
        return account == Account.Subscription
            ? new(Ttl.OneHour, "subscription default")
            : new(Ttl.FiveMinutes, "account default");
    }

    /// <summary>
    /// What the traffic says about the lifetime, given what earlier requests already showed
    /// (<paramref name="known"/>, null until something was proven). A hit after more than 5 minutes
    /// proves one hour and sticks; a lapse with an unchanged prompt says 5 minutes (a later hit overrules it).
    /// </summary>
    public static Ttl? ObserveTtl(Sample? prev, Sample cur, Ttl? known)
    {
        if (prev == null || prev.Read + prev.Write == 0 || prev.Model != cur.Model) return known;
        var gap = cur.StartedAt - prev.StartedAt;
        var before = PromptTokens(prev);
        if (gap <= FiveMinutesMs + SlackMs) return known;
        if (cur.Read >= before * 0.5) return Ttl.OneHour;
        if (known == Ttl.OneHour) return known;
        var lapsed = cur.Write > 0 && PromptTokens(cur) >= before * 0.7 && gap < OneHourMs + SlackMs;
        return lapsed ? Ttl.FiveMinutes : known;
    }

    /// <summary>Why the last request missed the cache, or null when it did not.</summary>
    public static string? MissReason(Sample? prev, Sample cur, Ttl ttl)
    {
        if (prev == null) return null;
        var before = PromptTokens(prev);
        // A shrinking prompt is /compact or /clear, not a miss.
        if (before == 0 || PromptTokens(cur) < before * 0.7) return null;
        if (cur.Read >= before * 0.5 || cur.Write == 0) return null;
        if (cur.Model != prev.Model) return $"model changed ({prev.Model} → {cur.Model})";
        if (cur.StartedAt - prev.StartedAt > TtlMs(ttl)) return $"the {TtlLabel(ttl)} cache lifetime had expired";
        return "the start of the prompt changed (effort, tools, system prompt or CLAUDE.md)";
    }

    // ---- countdown ---------------------------------------------------------------

    public static long ExpiresAt(Sample s, Ttl ttl) => s.StartedAt + TtlMs(ttl);

    public static long RemainingMs(Sample s, Ttl ttl, long now) =>
        s.Read + s.Write == 0 ? 0 : Math.Max(0, ExpiresAt(s, ttl) - now);

    public static double LifeRatio(long leftMs, Ttl ttl) => Math.Clamp((double)leftMs / TtlMs(ttl), 0, 1);

    public static LifeColor LifeColorOf(long leftMs, Ttl ttl, long warnMs)
    {
        if (leftMs <= warnMs) return LifeColor.Red;
        return (double)leftMs / TtlMs(ttl) <= 0.4 ? LifeColor.Yellow : LifeColor.Green;
    }

    /// <summary>
    /// The next toast mark that has been crossed and not yet shown, or null.
    /// <paramref name="level"/> is the last mark shown for this cache entry (start with int.MaxValue).
    /// </summary>
    public static int? NextToastMark(int secsLeft, int warnSecs, int level)
    {
        var marks = new[] { warnSecs }.Concat(CountdownMarks).Where(m => m <= warnSecs);
        var due = marks.Where(m => secsLeft <= m && m < level).ToList();
        return due.Count == 0 ? null : due.Min();
    }

    // ---- advice ------------------------------------------------------------------

    public static Advice Advise(Sample? last, Sample? prev, Policy policy, long now, bool disabled)
    {
        if (disabled) return new(AdviceKind.Off, "Prompt cache is off for this model.");
        if (last == null) return new(AdviceKind.Cold, "No requests yet.");
        if (last.Read + last.Write == 0) return new(AdviceKind.Uncached, "The last request did not use the cache.");

        var miss = MissReason(prev, last, policy.Ttl);
        var left = RemainingMs(last, policy.Ttl, now);
        var size = PromptTokens(last);

        if (left <= 0)
        {
            return size >= policy.CompactAtTokens
                ? new(AdviceKind.Expired, $"Cache expired. The next request rewrites ~{FmtTokens(size)} tokens: run /compact, or /clear if you are done.")
                : new(AdviceKind.Expired, $"Cache expired but the rewrite is small (~{FmtTokens(size)}); you can carry on.");
        }
        if (left <= policy.WarnMs) return new(AdviceKind.Soon, $"Cache expires in {FmtClock(left)}.");
        if (miss != null) return new(AdviceKind.Miss, $"Cache miss: {miss}.");
        return new(AdviceKind.Warm, "Cache is warm.");
    }

    // ---- grouping and drawing ----------------------------------------------------

    /// <summary>Groups adjacent samples of the same turn; input is oldest first.</summary>
    public static IReadOnlyList<TurnRow> ByTurn(IEnumerable<Sample> samples)
    {
        var rows = new List<TurnRow>();
        foreach (var s in samples)
        {
            if (rows.Count > 0 && rows[^1].TurnId == s.TurnId)
            {
                var r = rows[^1];
                rows[^1] = r with { Steps = r.Steps + 1, Read = r.Read + s.Read, Write = r.Write + s.Write, Fresh = r.Fresh + s.Fresh, Output = r.Output + s.Output };
            }
            else
            {
                rows.Add(new TurnRow(s.TurnId, 1, s.Read, s.Write, s.Fresh, s.Output));
            }
        }
        return rows;
    }

    /// <summary>Splits <paramref name="width"/> cells between read, write and fresh tokens.</summary>
    public static int[] Segments(long read, long write, long fresh, int width)
    {
        var parts = new[] { read, write, fresh };
        var total = parts.Sum();
        if (total == 0 || width <= 0) return [0, 0, 0];

        var cells = parts.Select(p => p == 0 ? 0 : Math.Max(1, (int)Math.Round((double)p / total * width, MidpointRounding.AwayFromZero))).ToArray();
        var over = cells.Sum() - width;
        while (over != 0)
        {
            if (over > 0)
            {
                cells[Array.IndexOf(cells, cells.Max())]--;
                over--;
            }
            else
            {
                cells[Array.IndexOf(parts, parts.Max())]++;
                over++;
            }
        }
        return cells;
    }

    // ---- formatting --------------------------------------------------------------

    public static string FmtTokens(long n)
    {
        var inv = CultureInfo.InvariantCulture;
        if (n < 1000) return n.ToString(inv);
        if (n < 100_000) return TrimZero((n / 1000.0).ToString("0.0", inv)) + "k";
        if (n < 1_000_000) return Math.Round(n / 1000.0, MidpointRounding.AwayFromZero).ToString("0", inv) + "k";
        return TrimZero((n / 1_000_000.0).ToString("0.0", inv)) + "M";
    }

    public static string FmtClock(long ms)
    {
        var total = (long)Math.Ceiling(Math.Max(0, ms) / 1000.0);
        long h = total / 3600, m = total % 3600 / 60, s = total % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    public static bool IsCachingDisabled(CacheEnv env, string model)
    {
        if (IsOn(env.DisableAll)) return true;
        var m = model.ToLowerInvariant();
        return (m.Contains("haiku") && IsOn(env.DisableHaiku))
            || (m.Contains("sonnet") && IsOn(env.DisableSonnet))
            || (m.Contains("opus") && IsOn(env.DisableOpus));
    }

    private static string TrimZero(string s) => s.EndsWith(".0", StringComparison.Ordinal) ? s[..^2] : s;
}
