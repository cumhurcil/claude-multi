namespace ClaudeMulti.Core;

/// <summary>Append-only log file (restarted past 1 MB) plus an event for the UI.</summary>
public sealed class ManagerLog(string path)
{
    private const long MaxBytes = 1 << 20;
    private readonly object gate = new();

    public event Action<string>? Written;

    public void Info(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Logging must never break the app.
            }
        }
        Written?.Invoke(line);
    }
}
