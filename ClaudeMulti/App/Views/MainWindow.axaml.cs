using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ClaudeMulti.App.ViewModels;
using ClaudeMulti.Core;

namespace ClaudeMulti.App.Views;

public partial class MainWindow : Window, IDialogs
{
    private static readonly (string Title, string Subtitle)[] Pages =
    [
        ("Instances", "As many Claude Desktop copies as you like at once, each with a different account."),
        ("Sync", "Chats and settings stay the same in every instance."),
        ("Sign-in routing", "Makes the right instance open when you sign in from the browser."),
        ("Prompt cache", "Watch the cache lifetime, get warned before it runs out, and manage Claude Code's cache settings here."),
        ("Log", "What the manager has done."),
        ("Transfer", "Move all accounts, chats and settings to another PC in one password-protected file."),
    ];

    private readonly Control[] pages;
    private bool exiting;

    public MainWindow()
    {
        InitializeComponent();
        pages = [PageInstances, PageSync, PageLogin, PageCache, PageLog, PageTransfer];
        ShowPage(0);
    }

    /// <summary>True when closing the window only hides it (there is a tray icon to bring it back); otherwise closing quits.</summary>
    public bool HideOnClose { get; init; } = true;

    public event Action? ExitRequested;

    /// <summary>Really closes the window (the Exit button and the tray menu).</summary>
    public void Quit()
    {
        exiting = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Only the user's own close click hides the window; a system quit (Cmd+Q, logout) must go through.
        if (!exiting && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            if (HideOnClose) Hide();
            else ExitRequested?.Invoke();
        }
        base.OnClosing(e);
    }

    private void Nav_Checked(object? sender, RoutedEventArgs e)
    {
        if (pages == null || sender is not RadioButton { IsChecked: true, Tag: string tag }) return;
        ShowPage(int.Parse(tag));
    }

    /// <summary>Opens a page by number (0 instances, 1 sync, 2 sign-in, 3 cache, 4 log, 5 transfer) and checks its nav button.</summary>
    public void GoTo(int index)
    {
        if (index < 0 || index >= pages.Length) return;
        ((RadioButton)NavPanel.Children[index]).IsChecked = true;
    }

    private void ShowPage(int index)
    {
        for (var i = 0; i < pages.Length; i++) pages[i].IsVisible = i == index;
        PageTitle.Text = Pages[index].Title;
        PageSubtitle.Text = Pages[index].Subtitle;
    }

    private void Exit_Click(object? sender, RoutedEventArgs e) => ExitRequested?.Invoke();

    // ---- prompt cache number boxes: applied when focus leaves or Enter is pressed, never while typing ----

    private CacheViewModel? Cache => (DataContext as MainViewModel)?.Cache;

    private void Warn_Commit(object? sender, RoutedEventArgs e)
    {
        if (Cache is not { } cache) return;
        if (int.TryParse(WarnBox.Text?.Trim(), out var value)) cache.WarnSeconds = value;
        WarnBox.Text = cache.WarnSeconds.ToString();
    }

    private void Compact_Commit(object? sender, RoutedEventArgs e)
    {
        if (Cache is not { } cache) return;
        if (long.TryParse(CompactBox.Text?.Trim().Replace(".", "").Replace(",", ""), out var value)) cache.CompactAtTokens = value;
        CompactBox.Text = cache.CompactAtTokens.ToString();
    }

    private void Warn_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Warn_Commit(sender, e);
    }

    private void Compact_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Compact_Commit(sender, e);
    }

    // ---- IDialogs ----

    /// <summary>A modal dialog needs a visible owner; the window may be hidden in the tray when something asks.</summary>
    private Window DialogOwner()
    {
        if (!IsVisible) Show();
        return this;
    }

    public async Task<bool> Confirm(string title, string message) =>
        await new MessageDialog(title, message, confirm: true).ShowDialog<bool>(DialogOwner());

    public async Task Alert(string title, string message) =>
        await new MessageDialog(title, message, confirm: false).ShowDialog<bool>(DialogOwner());

    public async Task Inform(string title, string message) =>
        await new MessageDialog(title, message, confirm: false).ShowDialog<bool>(DialogOwner());

    public async Task<string?> SaveFile(string title, string description, string extension, string fileName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = fileName,
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(description) { Patterns = [$"*.{extension}"] }, FilePickerFileTypes.All],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> OpenFile(string title, string description, string extension)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(description) { Patterns = [$"*.{extension}"] }, FilePickerFileTypes.All],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> Prompt(string title, string label, string initial, Func<string, string?> validate)
    {
        var dialog = new InputDialog(title, label, initial, validate);
        return await dialog.ShowDialog<bool>(DialogOwner()) ? dialog.Value : null;
    }

    public async Task<ApiSettings?> EditApi(string title, ApiSettings current)
    {
        var dialog = new ApiKeyDialog(title, current);
        return await dialog.ShowDialog<bool>(DialogOwner()) ? dialog.Value : null;
    }
}
