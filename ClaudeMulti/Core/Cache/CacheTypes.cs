namespace ClaudeMulti.Core.Cache;

public enum Ttl { FiveMinutes, OneHour }

public enum Account { Subscription, Credits, Other }

public enum AdviceKind { Off, Cold, Uncached, Warm, Soon, Expired, Miss }

public enum LifeColor { Green, Yellow, Red }

/// <summary>One main-loop model request: how its prompt was served by the cache.</summary>
public sealed record Sample(
    string TurnId,
    string Model,
    long StartedAt,
    long Read,
    long Write,
    long Fresh,
    long Output,
    Ttl? WrittenTtl = null);

public sealed record Policy(Ttl Ttl, long WarnMs, long CompactAtTokens);

public sealed record Advice(AdviceKind Kind, string Text);

public sealed record TurnRow(string TurnId, int Steps, long Read, long Write, long Fresh, long Output);

public sealed record TtlDecision(Ttl Ttl, string Source);

/// <summary>Prompt-cache related environment settings (names as Claude Code reads them).</summary>
public sealed record CacheEnv(
    string? Enable1h = null,
    string? Force5m = null,
    string? TtlVar = null,
    string? DisableAll = null,
    string? DisableHaiku = null,
    string? DisableSonnet = null,
    string? DisableOpus = null);

/// <summary>User options of the cache meter.</summary>
public sealed record CacheOptions(
    Ttl? Ttl = null,
    int WarnSeconds = 60,
    long CompactAtTokens = 100_000,
    bool Toast = true);
