using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeMulti.Core.Transfer;

/// <summary>An opened bundle: the decrypted ZIP and its manifest. Nothing is extracted until asked.</summary>
internal sealed class TransferBundle : IDisposable
{
    private readonly FileStream file;
    private readonly DecryptingStream decrypted;

    private TransferBundle(FileStream file, DecryptingStream decrypted, ZipArchive zip, TransferManifest manifest)
    {
        this.file = file;
        this.decrypted = decrypted;
        Zip = zip;
        Manifest = manifest;
    }

    public ZipArchive Zip { get; }
    public TransferManifest Manifest { get; }

    public static TransferBundle Open(string path, string passphrase)
    {
        FileStream? file = null;
        DecryptingStream? decrypted = null;
        ZipArchive? zip = null;
        try
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            decrypted = new DecryptingStream(file, passphrase, leaveOpen: true);
            zip = new ZipArchive(decrypted, ZipArchiveMode.Read, leaveOpen: true);

            var entry = zip.GetEntry(TransferService.ManifestEntry) ?? throw new TransferException("The transfer bundle has no manifest.");
            TransferManifest? manifest;
            using (var stream = entry.Open()) manifest = JsonSerializer.Deserialize<TransferManifest>(stream);
            if (manifest == null) throw new TransferException("Could not read the transfer bundle's manifest.");
            if (manifest.Format != TransferManifest.CurrentFormat)
                throw new TransferException("This bundle was created by a newer version; update the manager.");
            return new TransferBundle(file, decrypted, zip, manifest);
        }
        catch (Exception e) when (e is InvalidDataException or JsonException)
        {
            Close(zip, decrypted, file);
            throw new TransferException("The transfer bundle is corrupt.", e);
        }
        catch
        {
            Close(zip, decrypted, file);
            throw;
        }
    }

    private static void Close(ZipArchive? zip, DecryptingStream? decrypted, FileStream? file)
    {
        zip?.Dispose();
        decrypted?.Dispose();
        file?.Dispose();
    }

    public void Dispose() => Close(Zip, decrypted, file);
}

/// <summary>
/// Moves chats, settings and sign-ins of every Claude copy on this PC (plus Claude Code's own folder) into one
/// passphrase-protected file and back out on another PC.
/// <para>
/// The sign-in tokens and cookies are encrypted with a key that only this Windows user on this PC can unlock.
/// So the bundle carries that raw key too, and an import protects it again for the new PC's user. That is the only
/// way an account stays signed in after the move, and it is why the bundle must be treated like a password.
/// </para>
/// </summary>
public sealed partial class TransferService(ClaudeEnvironment env, IKeyProtector protector)
{
    public const string ManifestEntry = "manifest.json";
    public const int MinPassphraseLength = 8;
    private const int MaxWarnings = 40;

    private static readonly JsonSerializerOptions ManifestOptions = new() { WriteIndented = true };

    /// <summary>False where Claude Desktop's encryption key cannot be moved (macOS, Linux): sign-ins stay on their computer.</summary>
    public bool SupportsSignIns => protector.SupportsChromiumKey;

    public const string SignInsWindowsOnly =
        "Moving sign-ins works on Windows only: Claude Desktop keeps its encryption key in the macOS Keychain or the Linux secret service there, where it cannot be handed over. Sign in again on this computer.";

    public static string OsName(HostOs os) => os switch { HostOs.Windows => "windows", HostOs.MacOS => "macos", _ => "linux" };

    // ---- export ----

    public ExportResult Export(
        string outputPath, string passphrase, TransferScope scope,
        IReadOnlyDictionary<string, string> labels, Action<string>? report = null)
    {
        RequirePassphrase(passphrase);
        if (!scope.Any) throw new TransferException("Nothing was chosen to move.");
        if (scope.SignIns && !SupportsSignIns) throw new TransferException(SignInsWindowsOnly);

        var instances = InstanceCatalog.List(env);
        if (scope.SignIns) RequireClosed(instances, "sign-ins and cookies can only be copied consistently while closed");

        var warnings = new List<string>();
        var manifest = new TransferManifest
        {
            CreatedAt = DateTimeOffset.Now,
            Machine = Environment.MachineName,
            Os = OsName(HostPlatform.Current),
            SignIns = scope.SignIns,
            Chats = scope.Chats,
            Settings = scope.Settings,
        };
        foreach (var instance in instances)
        {
            manifest.Instances.Add(new ManifestInstance
            {
                Id = instance.Id,
                Label = labels.TryGetValue(instance.Id, out var label) ? label : null,
                Account = InstanceProbe.SignedInAccount(instance),
                Key = scope.SignIns ? ReadKey(instance, warnings) : null,
            });
        }

        report?.Invoke("Scanning files…");
        var entries = TransferCatalog.Enumerate(env, instances, scope, warnings).ToList();
        var configs = ReadConfigs(instances, scope, warnings);

        foreach (var entry in entries) AddStats(manifest, entry.Kind, entry.Length);
        foreach (var config in configs) AddStats(manifest, scope.Settings ? TransferKind.Settings : TransferKind.SignIn, config.Json.Length);

        var part = outputPath + ".part";
        var written = 0;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var encrypted = new EncryptingStream(file, passphrase, leaveOpen: true))
            {
                using (var zip = new ZipArchive(encrypted, ZipArchiveMode.Create, leaveOpen: true))
                {
                    AddText(zip, ManifestEntry, JsonSerializer.Serialize(manifest, ManifestOptions));
                    foreach (var config in configs)
                    {
                        AddText(zip, config.Name, config.Json);
                        written++;
                    }
                    foreach (var entry in entries)
                    {
                        if (AddFile(zip, entry, warnings)) written++;
                        if (written % 250 == 0) report?.Invoke($"Writing bundle… {written}/{entries.Count + configs.Count} files");
                    }
                }
                encrypted.Complete();
            }
            File.Move(part, outputPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(part); }
            catch (IOException) { /* a leftover .part file is harmless */ }
            throw;
        }

        return new ExportResult(outputPath, written, new FileInfo(outputPath).Length, manifest, Capped(warnings));
    }

    /// <summary>Opens a bundle with its passphrase and returns what it holds. Changes nothing.</summary>
    public TransferManifest Inspect(string bundlePath, string passphrase)
    {
        using var bundle = TransferBundle.Open(bundlePath, passphrase);
        return bundle.Manifest;
    }

    private string? ReadKey(InstanceInfo instance, List<string> warnings)
    {
        var key = ChromiumKey.Read(ChromiumKey.LocalStatePath(instance), protector);
        if (key != null) return Convert.ToBase64String(key);
        if (InstanceProbe.IsSignedIn(instance))
            warnings.Add($"{DisplayName(instance.Id)}: encryption key not found; the sign-in will not work on another PC.");
        return null;
    }

    private static List<(string Name, string Json)> ReadConfigs(IReadOnlyList<InstanceInfo> instances, TransferScope scope, List<string> warnings)
    {
        var result = new List<(string, string)>();
        foreach (var instance in instances)
        {
            var (state, config) = JsonFile.Read(instance.ConfigPath);
            if (state == DocState.Invalid)
            {
                warnings.Add($"{DisplayName(instance.Id)}: could not read config.json; settings and sign-in not moved.");
                continue;
            }
            if (state != DocState.Ok) continue;
            var filtered = FilterConfig(config!, scope);
            if (filtered.Count > 0)
                result.Add(($"{TransferRoots.Instances}/{instance.Id}/config.json", filtered.ToJsonString()));
        }
        return result;
    }

    /// <summary>The part of config.json the selected kinds cover: the account's tokens and/or the user's settings.</summary>
    internal static JsonObject FilterConfig(JsonObject config, TransferScope scope)
    {
        var result = new JsonObject();
        foreach (var (key, value) in config)
        {
            var wanted = SettingsSync.IsAuthKey(key) ? scope.SignIns : scope.Settings && SettingsSync.IsPortableKey(key);
            if (wanted) result[key] = value?.DeepClone();
        }
        return result;
    }

    private static void AddStats(TransferManifest manifest, TransferKind kind, long bytes)
    {
        var name = kind.ToString();
        if (!manifest.Stats.TryGetValue(name, out var stats)) manifest.Stats[name] = stats = new KindStats();
        stats.Files++;
        stats.Bytes += bytes;
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static bool AddFile(ZipArchive zip, TransferEntry entry, List<string> warnings)
    {
        FileStream source;
        try { source = new FileStream(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not read, skipped: {entry.Name}");
            return false;
        }
        using (source)
        {
            var zipEntry = zip.CreateEntry(entry.Name, CompressionLevel.Fastest);
            zipEntry.LastWriteTime = DosTime(entry.ModifiedUtc);
            using var target = zipEntry.Open();
            source.CopyTo(target);
        }
        return true;
    }

    /// <summary>ZIP stores local clock time between 1980 and 2107.</summary>
    private static DateTimeOffset DosTime(DateTime utc)
    {
        var min = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var max = new DateTime(2107, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        return new DateTimeOffset(DateTime.SpecifyKind(utc < min ? min : utc > max ? max : utc, DateTimeKind.Utc).ToLocalTime());
    }

    // ---- shared ----

    private static void RequirePassphrase(string passphrase)
    {
        if (passphrase.Length < MinPassphraseLength)
            throw new TransferException($"The password must be at least {MinPassphraseLength} characters.");
    }

    private static void RequireClosed(IEnumerable<InstanceInfo> instances, string reason)
    {
        var running = instances.Where(InstanceProbe.IsRunning).Select(i => DisplayName(i.Id)).ToList();
        if (running.Count > 0)
            throw new TransferException($"Close these first ({reason}): {string.Join(", ", running)}.");
    }

    internal static string DisplayName(string id) => id == ClaudeEnvironment.MainId ? "Main Claude" : $"Instance {id}";

    private static IReadOnlyList<string> Capped(List<string> warnings) =>
        warnings.Count <= MaxWarnings
            ? warnings
            : [.. warnings.Take(MaxWarnings), $"…and {warnings.Count - MaxWarnings} more warning(s)."];
}
