using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeMulti.Core;

public enum DocState { Missing, Invalid, Ok }

/// <summary>Tolerant JSON object reader and atomic writer for Claude's config files.</summary>
public static class JsonFile
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        IndentCharacter = '\t',
        IndentSize = 1,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static (DocState State, JsonObject? Obj) Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonNode.Parse(fs) is JsonObject obj ? (DocState.Ok, obj) : (DocState.Invalid, null);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return (DocState.Missing, null);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return (DocState.Invalid, null);
        }
    }

    public static JsonObject? ReadOrNull(string path) => Read(path).Obj;

    private static readonly JsonSerializerOptions SpaceOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Atomic write. Claude Desktop's own files use tabs; Claude Code's settings.json uses two spaces.</summary>
    public static void Write(string path, JsonObject obj, bool tabs = true)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".manager-tmp";
        File.WriteAllText(tmp, obj.ToJsonString(tabs ? WriteOptions : SpaceOptions), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Atomic write without indentation, the form Chromium writes its "Local State" in.</summary>
    public static void WriteCompact(string path, JsonObject obj)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".manager-tmp";
        File.WriteAllText(tmp, obj.ToJsonString(CompactOptions), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static readonly JsonSerializerOptions CompactOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Compact JSON text of a node, the form values are compared and stored in.</summary>
    public static string Canon(JsonNode? node) => node?.ToJsonString() ?? "null";
}
