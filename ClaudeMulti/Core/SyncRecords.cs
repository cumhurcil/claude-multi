using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeMulti.Core;

/// <summary>
/// What the settings sync last saw or applied, per file, instance and key (as value hashes).
/// A value that differs from its record was changed by the user since the last sync.
/// </summary>
public sealed class SyncRecords
{
    public Dictionary<string, Dictionary<string, Dictionary<string, string>>> Data { get; set; } = new();

    public string? Get(string file, string instance, string key) =>
        Data.TryGetValue(file, out var f) && f.TryGetValue(instance, out var i) && i.TryGetValue(key, out var h) ? h : null;

    public void Set(string file, string instance, string key, string? hash)
    {
        if (!Data.TryGetValue(file, out var f)) Data[file] = f = new();
        if (!f.TryGetValue(instance, out var i)) f[instance] = i = new();
        if (hash == null) i.Remove(key); else i[key] = hash;
    }

    public static string Hash(string canon) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(canon)))[..16];

    public static SyncRecords Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<SyncRecords>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this));
        File.Move(tmp, path, overwrite: true);
    }
}
