using System.Text.Json;

namespace ClaudeMulti.Core;

/// <summary>Everything the manager remembers between runs. Shared by the UI and the claude:// handler.</summary>
public sealed class ManagerState
{
    public bool SyncChats { get; set; } = true;
    public bool SyncSettings { get; set; } = true;
    public bool AutoSync { get; set; } = true;
    public int IntervalSeconds { get; set; } = 60;

    public bool HookEnabled { get; set; }
    public string? OriginalProtocolCommand { get; set; }

    /// <summary>The instance a browser sign-in should be delivered to, until <see cref="PendingSince"/> + TTL.</summary>
    public string? PendingLoginId { get; set; }
    public DateTimeOffset? PendingSince { get; set; }

    public Dictionary<string, string> Labels { get; set; } = new();

    // Prompt cache meter options. CacheTtl: "auto", "5m" or "1h". CacheAccount: "subscription", "credits" or "other".
    public string CacheTtl { get; set; } = "auto";
    public string CacheAccount { get; set; } = "other";
    public int CacheWarnSeconds { get; set; } = 60;
    public long CacheCompactAtTokens { get; set; } = 100_000;
    public bool CacheToast { get; set; } = true;
}

public sealed class StateStore(string path)
{
    public ManagerState Load()
    {
        try
        {
            return JsonSerializer.Deserialize<ManagerState>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    /// <summary>Loads, changes and saves the state under a per-user lock (the UI and the handler both write it).</summary>
    public void Update(Action<ManagerState> change)
    {
        using var gate = StateLock.Acquire(path, TimeSpan.FromSeconds(3));
        var state = Load();
        change(state);
        Save(state);
    }

    private void Save(ManagerState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        // A virus scanner or indexer can hold the file for a moment right after it is written; Windows then refuses the rename.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 5)
            {
                Thread.Sleep(25 * attempt);
            }
        }
    }
}

/// <summary>
/// Keeps two processes from rewriting the state file at once. Windows uses a named mutex; a named mutex on
/// macOS and Linux is shared machine-wide and breaks when two users run the app, so there a lock file is used.
/// Waiting longer than <c>wait</c> proceeds without the lock rather than hanging the app.
/// </summary>
internal sealed class StateLock : IDisposable
{
    private const string MutexName = @"Global\ClaudeMultiState";

    private Mutex? mutex;
    private FileStream? file;

    private StateLock() { }

    public static StateLock Acquire(string statePath, TimeSpan wait) => Acquire(statePath, wait, useFile: !HostPlatform.IsWindows);

    internal static StateLock Acquire(string statePath, TimeSpan wait, bool useFile)
    {
        var gate = new StateLock();
        if (useFile) gate.TakeFile(statePath + ".lock", wait);
        else gate.TakeMutex(wait);
        return gate;
    }

    private void TakeMutex(TimeSpan wait)
    {
        var created = new Mutex(false, MutexName);
        try
        {
            if (created.WaitOne(wait)) mutex = created;
        }
        catch (AbandonedMutexException)
        {
            mutex = created;
        }
        if (mutex == null) created.Dispose();
    }

    private void TakeFile(string lockPath, TimeSpan wait)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                file = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        if (mutex != null)
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
        file?.Dispose();
    }
}
