namespace ClaudeMulti.Core.Cache;

/// <summary>Everything the cache page shows for one conversation at one moment.</summary>
public sealed record CacheView(
    Sample? Last,
    Ttl Ttl,
    string TtlSource,
    Advice Advice,
    long LeftMs,
    double LifeRatio,
    LifeColor Color,
    double HitPercent,
    IReadOnlyList<TurnRow> Rows);

public static class CacheAnalyzer
{
    /// <summary>Rows kept for the table, newest last.</summary>
    public const int MaxRows = 40;

    public static CacheView Analyze(
        IReadOnlyList<Sample> samples,
        CacheOptions options,
        CacheEnv env,
        string? settingTtl,
        Account account,
        long now)
    {
        var decision = CacheMath.DecideTtl(options.Ttl, env, settingTtl, account);
        var ttl = decision.Ttl;
        var source = decision.Source;

        if (options.Ttl == null)
        {
            // Traffic can prove the lifetime; the usage record of a write states it outright.
            Ttl? observed = null;
            for (var i = 1; i < samples.Count; i++)
                observed = CacheMath.ObserveTtl(samples[i - 1], samples[i], observed);
            if (observed is { } seen)
            {
                ttl = seen;
                source = $"inferred from traffic; {decision.Source} said {CacheMath.TtlLabel(decision.Ttl)}";
            }
            var written = samples.LastOrDefault(s => s.WrittenTtl != null)?.WrittenTtl;
            if (written is { } exact) { ttl = exact; source = "usage record"; }
        }

        var last = samples.Count > 0 ? samples[^1] : null;
        var prev = samples.Count > 1 ? samples[^2] : null;
        var policy = new Policy(ttl, options.WarnSeconds * 1000L, options.CompactAtTokens);
        var disabled = last != null && CacheMath.IsCachingDisabled(env, last.Model);
        var advice = CacheMath.Advise(last, prev, policy, now, disabled);

        var left = last == null ? 0 : CacheMath.RemainingMs(last, ttl, now);
        var rows = CacheMath.ByTurn(samples);
        if (rows.Count > MaxRows) rows = rows.Skip(rows.Count - MaxRows).ToList();

        return new CacheView(
            last, ttl, source, advice, left,
            CacheMath.LifeRatio(left, ttl),
            CacheMath.LifeColorOf(left, ttl, policy.WarnMs),
            last == null ? 0 : CacheMath.HitRatio(last) * 100,
            rows);
    }
}
