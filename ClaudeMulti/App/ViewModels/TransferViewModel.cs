using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using ClaudeMulti.App.Infrastructure;
using ClaudeMulti.Core;
using ClaudeMulti.Core.Transfer;

namespace ClaudeMulti.App.ViewModels;

/// <summary>The "Transfer" page: pack every account's chats, settings and sign-ins into one protected file, or unpack one.</summary>
public sealed class TransferViewModel : ObservableObject
{
    private const string BundleDescription = "Claude transfer bundle";
    private const string BundleExtension = "cmxfer";

    private readonly ClaudeEnvironment env;
    private readonly StateStore store;
    private readonly ManagerLog log;
    private readonly IDialogs dialogs;
    private readonly TransferService service;
    private readonly Action afterImport;

    private bool exportSignIns, exportChats = true, exportSettings = true;
    private bool importSignIns, importChats, importSettings;
    private string exportPassword = "", exportPasswordRepeat = "", importPassword = "";
    private string importPath = "";
    private string preview = "";
    private string status = "";
    private bool busy;
    private TransferManifest? opened;

    public TransferViewModel(ClaudeEnvironment env, StateStore store, ManagerLog log, IDialogs dialogs, IKeyProtector protector, Action afterImport)
    {
        this.env = env;
        this.store = store;
        this.log = log;
        this.dialogs = dialogs;
        this.afterImport = afterImport;
        service = new TransferService(env, protector);
        exportSignIns = service.SupportsSignIns;

        ExportCommand = new RelayCommand(Export, () => !busy).Tracked();
        BrowseCommand = new RelayCommand(Browse, () => !busy).Tracked();
        InspectCommand = new RelayCommand(Inspect, () => !busy && importPath.Length > 0 && importPassword.Length > 0).Tracked();
        ImportCommand = new RelayCommand(Import, () => !busy && opened != null && ImportScope.Any).Tracked();
    }

    public ICommand ExportCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand InspectCommand { get; }
    public ICommand ImportCommand { get; }

    /// <summary>Sign-ins can only move between Windows PCs (see <see cref="TransferService.SignInsWindowsOnly"/>).</summary>
    public bool SignInsSupported => service.SupportsSignIns;
    public bool SignInsUnsupported => !service.SupportsSignIns;
    public string SignInsUnsupportedText => TransferService.SignInsWindowsOnly;

    public string GoodToKnow =>
        (service.SupportsSignIns
            ? "• Account sign-ins are stored encrypted and only this Windows user can open them. So the bundle carries that key too and protects it again for that user on the new PC. A bundle is as valuable as a password: do not share it with anyone.\n"
            : "• Sign-ins stay on their computer: only Windows can hand over Claude Desktop's encryption key. Chats and settings move normally. A bundle is still as valuable as a password: do not share it with anyone.\n") +
        "• The related Claude windows must be closed when moving sign-ins, and also when importing.\n" +
        "• Caches, logs and things specific to this PC (window position, update state) are not moved.\n" +
        $"• Every file an import changes is first backed up under {env.ImportBackupsDir}; if it stops halfway, everything is rolled back.\n" +
        "• Chat records contain folder paths; on a PC with a different user name, Claude Code may not match conversations of projects at the old paths. If project folders sit at the same path, it works fine.\n" +
        "• A bundle made on another operating system keeps its settings as they were: paths in them (for example MCP server commands) may not exist here.";

    /// <summary>Typed into password boxes; kept only in memory and cleared after use, never saved.</summary>
    public string ExportPassword { get => exportPassword; set => Set(ref exportPassword, value ?? ""); }
    public string ExportPasswordRepeat { get => exportPasswordRepeat; set => Set(ref exportPasswordRepeat, value ?? ""); }
    public string ImportPassword { get => importPassword; set { if (Set(ref importPassword, value ?? "")) RelayCommand.Requery(); } }

    public bool ExportSignIns { get => exportSignIns; set => Set(ref exportSignIns, value && service.SupportsSignIns); }
    public bool ExportChats { get => exportChats; set => Set(ref exportChats, value); }
    public bool ExportSettings { get => exportSettings; set => Set(ref exportSettings, value); }

    public bool ImportSignIns { get => importSignIns; set { if (Set(ref importSignIns, value && service.SupportsSignIns)) RelayCommand.Requery(); } }
    public bool ImportChats { get => importChats; set { if (Set(ref importChats, value)) RelayCommand.Requery(); } }
    public bool ImportSettings { get => importSettings; set { if (Set(ref importSettings, value)) RelayCommand.Requery(); } }

    public bool HasSignIns => opened?.SignIns == true && service.SupportsSignIns;
    public bool HasChats => opened?.Chats == true;
    public bool HasSettings => opened?.Settings == true;
    public bool HasPreview => opened != null;

    public string ImportPath { get => importPath; private set => Set(ref importPath, value); }
    public string Preview { get => preview; private set => Set(ref preview, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public bool IsBusy { get => busy; private set { if (Set(ref busy, value)) RelayCommand.Requery(); } }

    private TransferScope ImportScope => new(importSignIns && HasSignIns, importChats && HasChats, importSettings && HasSettings);

    // ---- export ----

    private async void Export()
    {
        var scope = new TransferScope(exportSignIns && service.SupportsSignIns, exportChats, exportSettings);
        if (!scope.Any)
        {
            await dialogs.Alert("Export", "Choose at least one kind to move.");
            return;
        }
        if (exportPassword.Length < TransferService.MinPassphraseLength)
        {
            await dialogs.Alert("Export", $"The password must be at least {TransferService.MinPassphraseLength} characters.");
            return;
        }
        if (exportPassword != exportPasswordRepeat)
        {
            await dialogs.Alert("Export", "The two passwords do not match.");
            return;
        }
        var path = await dialogs.SaveFile("Save transfer bundle", BundleDescription, BundleExtension, $"claude-transfer-{DateTime.Now:yyyyMMdd-HHmm}.{BundleExtension}");
        if (path == null) return;

        var password = exportPassword;
        var labels = store.Load().Labels;
        var result = await RunAsync("Could not export", "Preparing bundle…",
            () => service.Export(path, password, scope, labels, Progress));
        if (result == null) return;

        ExportPassword = "";
        ExportPasswordRepeat = "";
        var size = FormatSize(result.BundleBytes);
        Status = $"Bundle ready: {result.Path} · {result.Files} files · {size}";
        log.Info($"Transfer bundle created: {result.Path} ({result.Files} files, {size}).");
        foreach (var warning in result.Warnings) log.Info("Transfer warning: " + warning);
        await dialogs.Inform("Bundle ready",
            $"{result.Path}\n\n{result.Files} files, {size}.\n\nThe bundle may contain your sign-in data. It is protected by the password, but keep it somewhere safe; if you forget the password it cannot be opened." +
            (result.Warnings.Count > 0 ? $"\n\n{result.Warnings.Count} warning(s) (on the Log page)." : ""));
    }

    // ---- import ----

    private async void Browse()
    {
        var path = await dialogs.OpenFile("Choose transfer bundle", BundleDescription, BundleExtension);
        if (path == null) return;
        ImportPath = path;
        Forget();
        RelayCommand.Requery();
    }

    private async void Inspect()
    {
        var path = importPath;
        var password = importPassword;
        var manifest = await RunAsync("Could not open bundle", "Opening bundle…", () => service.Inspect(path, password));
        if (manifest == null)
        {
            Forget();
            return;
        }
        opened = manifest;
        RaiseAvailability();
        ImportSignIns = manifest.SignIns;
        ImportChats = manifest.Chats;
        ImportSettings = manifest.Settings;
        Preview = Describe(manifest);
        Status = "Bundle opened. Choose what to import and press 'Import'.";
        RaiseAvailability();
    }

    private async void Import()
    {
        if (opened == null) return;
        var scope = ImportScope;
        if (!await dialogs.Confirm("Import",
                "The selected data will overwrite the Claude data on this PC. Chats are merged, none are deleted.\n\n" +
                "Every changed file is backed up first; if anything goes wrong, everything is rolled back.\n" +
                "The related Claude windows must be closed.\n\nContinue?"))
            return;

        var path = importPath;
        var password = importPassword;
        var result = await RunAsync("Could not import", "Importing…", () => service.Import(path, password, scope, Progress));
        if (result == null) return;

        store.Update(s =>
        {
            foreach (var (id, label) in result.Labels)
                if (!s.Labels.TryGetValue(id, out var existing) || string.IsNullOrWhiteSpace(existing)) s.Labels[id] = label;
        });
        ImportPassword = "";
        var summary = $"{result.FilesWritten} files written, {result.FilesKept} chat files left as they are because they were already up to date.";
        Status = "Import finished. " + summary;
        log.Info($"Transfer bundle imported: {summary} Backup: {result.BackupDir}");
        foreach (var warning in result.Warnings) log.Info("Transfer warning: " + warning);
        Forget();
        afterImport();
        await dialogs.Inform("Import finished",
            $"{summary}\n\nBackup of the old files:\n{result.BackupDir}" +
            (service.SupportsSignIns ? "\n\nOpen Claude; the accounts should come up without asking to sign in." : "") +
            (result.Warnings.Count > 0 ? $"\n\n{result.Warnings.Count} warning(s) (on the Log page)." : ""));
    }

    /// <summary>Drops the opened bundle so a stale preview is never imported with another file's name on it.</summary>
    private void Forget()
    {
        opened = null;
        Preview = "";
        ImportSignIns = ImportChats = ImportSettings = false;
        RaiseAvailability();
    }

    private void RaiseAvailability()
    {
        Raise(nameof(HasSignIns));
        Raise(nameof(HasChats));
        Raise(nameof(HasSettings));
        Raise(nameof(HasPreview));
        RelayCommand.Requery();
    }

    private string Describe(TransferManifest manifest)
    {
        var text = new StringBuilder();
        var origin = manifest.Os.Length > 0 ? $" ({manifest.Os})" : "";
        text.AppendLine($"Created: {manifest.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm} · computer: {manifest.Machine}{origin}");
        text.AppendLine();
        foreach (var instance in manifest.Instances)
        {
            var name = string.IsNullOrWhiteSpace(instance.Label) ? TransferNameOf(instance.Id) : instance.Label;
            var account = instance.Account is { Length: >= 8 } id ? $" · account {id[..8]}" : "";
            var here = File.Exists(Path.Combine(env.DataDirOf(instance.Id), "config.json"))
                ? "exists on this PC, the chosen parts are overwritten"
                : "will be created on this PC";
            text.AppendLine($"• {name}{account} ({here})");
        }
        text.AppendLine();
        AppendStats(text, manifest, TransferKind.SignIn, service.SupportsSignIns ? "Sign-ins" : "Sign-ins (cannot be imported here)");
        AppendStats(text, manifest, TransferKind.Chats, "Chats");
        AppendStats(text, manifest, TransferKind.Settings, "Settings");
        return text.ToString().TrimEnd();
    }

    private static void AppendStats(StringBuilder text, TransferManifest manifest, TransferKind kind, string title)
    {
        var enabled = kind switch { TransferKind.SignIn => manifest.SignIns, TransferKind.Chats => manifest.Chats, _ => manifest.Settings };
        if (!enabled) return;
        var stats = manifest.StatsOf(kind);
        text.AppendLine($"{title}: {stats.Files} files, {FormatSize(stats.Bytes)}");
    }

    private static string TransferNameOf(string id) => id == ClaudeEnvironment.MainId ? "Main Claude" : $"Instance {id}";

    // ---- plumbing ----

    private void Progress(string message) => Dispatcher.UIThread.Post(() => Status = message);

    /// <summary>Runs slow work off the UI thread. Expected failures become a message; null means "did not work".</summary>
    private async Task<T?> RunAsync<T>(string failureTitle, string busyText, Func<T> work) where T : class
    {
        IsBusy = true;
        Status = busyText;
        try
        {
            return await Task.Run(work);
        }
        catch (Exception e) when (e is TransferException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = "Failed: " + e.Message;
            log.Info($"{failureTitle}: {e.Message}");
            await dialogs.Alert(failureTitle, e.Message);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} B",
    };
}
