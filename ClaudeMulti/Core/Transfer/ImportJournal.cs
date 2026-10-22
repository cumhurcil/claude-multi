using System.Text;

namespace ClaudeMulti.Core.Transfer;

/// <summary>
/// Remembers what an import changed. Every file or folder is copied to the backup folder before it is replaced,
/// and <see cref="Rollback"/> puts it all back if the import fails halfway. The backup stays on disk either way;
/// <c>index.txt</c> in it says which copy belongs to which original path.
/// </summary>
internal sealed class ImportJournal(string backupDir)
{
    private sealed record Item(string Target, string? Backup, bool IsDirectory);

    private readonly List<Item> items = [];
    private int counter;

    public string BackupDir => backupDir;

    /// <summary>Call before writing <paramref name="target"/>: keeps a copy of the old file, or notes that it is new.</summary>
    public void BeforeWrite(string target)
    {
        if (!File.Exists(target))
        {
            items.Add(new Item(target, null, false));
            return;
        }
        var backup = NextBackupPath(target);
        Directory.CreateDirectory(backupDir);
        File.Copy(target, backup, overwrite: true);
        Note(backup, target);
        items.Add(new Item(target, backup, false));
    }

    /// <summary>Moves a folder out of the way so the import can recreate it from the bundle.</summary>
    public void ReplaceDirectory(string target)
    {
        if (!Directory.Exists(target))
        {
            items.Add(new Item(target, null, true));
            return;
        }
        var backup = NextBackupPath(target);
        CopyDirectory(target, backup);
        Note(backup, target);
        items.Add(new Item(target, backup, true));
        Directory.Delete(target, recursive: true);
    }

    public void Remove(string file)
    {
        if (!File.Exists(file)) return;
        BeforeWrite(file);
        ClearReadOnly(file);
        File.Delete(file);
    }

    /// <summary>Returns the paths it could not restore, so the caller can tell the user where the backup is.</summary>
    public IReadOnlyList<string> Rollback()
    {
        var failed = new List<string>();
        foreach (var item in Enumerable.Reverse(items))
        {
            try
            {
                if (item.IsDirectory)
                {
                    if (Directory.Exists(item.Target)) Directory.Delete(item.Target, recursive: true);
                    if (item.Backup != null) CopyDirectory(item.Backup, item.Target);
                }
                else if (item.Backup != null)
                {
                    ClearReadOnly(item.Target);
                    File.Copy(item.Backup, item.Target, overwrite: true);
                }
                else if (File.Exists(item.Target))
                {
                    ClearReadOnly(item.Target);
                    File.Delete(item.Target);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed.Add(item.Target);
            }
        }
        return failed;
    }

    public static void ClearReadOnly(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* a missing file needs no change */ }
    }

    private string NextBackupPath(string target) =>
        Path.Combine(backupDir, $"{++counter:D5}-{Path.GetFileName(target)}");

    private void Note(string backup, string original) =>
        File.AppendAllText(Path.Combine(backupDir, "index.txt"), $"{Path.GetFileName(backup)}\t{original}{Environment.NewLine}", new UTF8Encoding(false));

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }
}
