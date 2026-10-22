using System.Text.Json.Serialization;

namespace ClaudeMulti.Core.Transfer;

/// <summary>One Claude copy inside a bundle.</summary>
public sealed class ManifestInstance
{
    public string Id { get; set; } = "";
    public string? Label { get; set; }

    /// <summary>The signed-in account's id (not secret), for showing what the bundle holds.</summary>
    public string? Account { get; set; }

    /// <summary>
    /// The raw key this copy's tokens and cookies are encrypted with, base64. Only present when sign-ins are
    /// included. It is as sensitive as the tokens, which is why the whole bundle is encrypted with a passphrase.
    /// </summary>
    public string? Key { get; set; }
}

public sealed class KindStats
{
    public int Files { get; set; }
    public long Bytes { get; set; }
}

/// <summary>The first entry of a bundle: what it holds, so it can be shown before anything is changed.</summary>
public sealed class TransferManifest
{
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;
    public DateTimeOffset CreatedAt { get; set; }
    public string Machine { get; set; } = "";

    /// <summary>"windows", "macos" or "linux": settings carry paths of the system they came from. Empty in older bundles.</summary>
    public string Os { get; set; } = "";
    public bool SignIns { get; set; }
    public bool Chats { get; set; }
    public bool Settings { get; set; }
    public List<ManifestInstance> Instances { get; set; } = [];
    public Dictionary<string, KindStats> Stats { get; set; } = [];

    [JsonIgnore]
    public TransferScope Scope => new(SignIns, Chats, Settings);

    public KindStats StatsOf(TransferKind kind) => Stats.TryGetValue(kind.ToString(), out var s) ? s : new KindStats();
}

public sealed record ExportResult(string Path, int Files, long BundleBytes, TransferManifest Manifest, IReadOnlyList<string> Warnings);

public sealed record ImportResult(
    int FilesWritten,
    int FilesKept,
    long Bytes,
    string BackupDir,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<string> Warnings);
