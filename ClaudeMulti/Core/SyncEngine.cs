namespace ClaudeMulti.Core;

public sealed record SyncReport(
    int Instances,
    int ChatsCopied,
    int SettingsKeysApplied,
    int InstancesWaitingForClose,
    DateTimeOffset At,
    IReadOnlyList<string> Errors)
{
    public bool HasChanges => ChatsCopied > 0 || SettingsKeysApplied > 0;
}

/// <summary>Runs chat and settings sync according to the saved options.</summary>
public sealed class SyncEngine(ClaudeEnvironment env, StateStore store)
{
    private readonly object gate = new();

    public SyncReport Run()
    {
        lock (gate)
        {
            var state = store.Load();
            // API instances keep their data in their own Claude-3p folder and have no claude.ai account to sync.
            var instances = InstanceCatalog.List(env).Where(i => !i.IsApi).ToList();
            var errors = new List<string>();
            int chats = 0, keys = 0, waiting = 0;

            if (state.SyncChats)
                Attempt("Sohbetler", errors, () => chats = ChatSync.Run(instances));

            if (state.SyncSettings)
            {
                Attempt("Ayarlar", errors, () =>
                {
                    var result = SettingsSync.Run(instances, InstanceProbe.IsRunning, env.SyncRecordPath, env.BackupsDir, DateTime.UtcNow);
                    keys = result.KeysApplied;
                    waiting = result.PendingInstances;
                });
            }
            return new SyncReport(instances.Count, chats, keys, waiting, DateTimeOffset.Now, errors);
        }
    }

    private static void Attempt(string what, List<string> errors, Action work)
    {
        try { work(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            errors.Add($"{what}: {e.Message}");
        }
    }
}
