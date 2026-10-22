using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ClaudeMulti.App.Infrastructure;
using ClaudeMulti.Core;

namespace ClaudeMulti.App.ViewModels;

public static class Palette
{
    public static readonly IBrush Good = Make(0x5D, 0xBB, 0x84);
    public static readonly IBrush Warn = Make(0xE8, 0xB0, 0x4A);
    public static readonly IBrush Bad = Make(0xE5, 0x60, 0x4E);
    public static readonly IBrush Muted = Make(0xA8, 0x9F, 0x96);
    public static readonly IBrush Info = Make(0x6F, 0xA8, 0xDC);

    private static IBrush Make(byte r, byte g, byte b) => new ImmutableSolidColorBrush(Color.FromRgb(r, g, b));
}

/// <summary>One Claude Desktop copy as a card.</summary>
public sealed class InstanceViewModel : ObservableObject
{
    private string label = "";
    private bool isRunning;
    private bool isSignedIn;
    private string account = "";
    private int chatCount;
    private bool isApi;
    private string apiSummary = "";

    public InstanceViewModel(InstanceInfo info, ICommand open, ICommand login, ICommand api, ICommand folder, ICommand rename, ICommand remove, ICommand close)
    {
        Info = info;
        OpenCommand = open;
        LoginCommand = login;
        ApiCommand = api;
        FolderCommand = folder;
        RenameCommand = rename;
        RemoveCommand = remove;
        CloseCommand = close;
    }

    public InstanceInfo Info { get; }
    public string Id => Info.Id;
    public bool IsMain => Info.IsMain;
    public bool CanRemove => !Info.IsMain;
    public string DataDir => Info.DataDir;

    public ICommand OpenCommand { get; }
    public ICommand LoginCommand { get; }
    public ICommand ApiCommand { get; }
    public ICommand FolderCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand CloseCommand { get; }

    public string Label { get => label; private set => Set(ref label, value); }
    public bool IsRunning { get => isRunning; private set { if (Set(ref isRunning, value)) { Raise(nameof(StatusText)); Raise(nameof(StatusBrush)); Raise(nameof(OpenText)); Raise(nameof(CanClose)); Raise(nameof(OpenTip)); } } }
    public bool IsSignedIn { get => isSignedIn; private set { if (Set(ref isSignedIn, value)) { Raise(nameof(AccountText)); Raise(nameof(AccountBrush)); } } }
    public int ChatCount { get => chatCount; private set { if (Set(ref chatCount, value)) Raise(nameof(ChatText)); } }
    public string Account { get => account; private set { if (Set(ref account, value)) Raise(nameof(AccountText)); } }

    public bool IsApi
    {
        get => isApi;
        private set { if (Set(ref isApi, value)) { Raise(nameof(AccountText)); Raise(nameof(AccountBrush)); Raise(nameof(ChatText)); Raise(nameof(LoginText)); Raise(nameof(LoginTip)); Raise(nameof(OpenText)); Raise(nameof(OpenTip)); } }
    }

    /// <summary>Gateway host and masked key, e.g. "openrouter.ai · sk-or-…3f9a".</summary>
    public string ApiSummary { get => apiSummary; private set { if (Set(ref apiSummary, value)) Raise(nameof(AccountText)); } }

    public string StatusText => IsRunning ? "Running" : "Closed";
    public IBrush StatusBrush => IsRunning ? Palette.Good : Palette.Muted;
    public string OpenText => IsApi ? "Open CLI" : IsRunning ? "Show" : "Open";
    public string OpenTip => IsApi ? "Open a new Claude Code (CLI) console window" : IsRunning ? "Bring this instance's window to the front" : "Start this instance";
    public string AccountText => IsApi ? $"API · {ApiSummary}" : IsSignedIn ? $"Signed in · account {Account}" : "Sign-in needed";
    public IBrush AccountBrush => IsApi ? Palette.Info : IsSignedIn ? Palette.Muted : Palette.Warn;
    public string ChatText => IsApi ? "Opens in a Claude Code (CLI) window; not synced" : IsSignedIn ? $"{ChatCount} chats" : "Chats appear after sign-in";
    public string LoginText => IsApi ? "API key" : "Sign in";
    public string LoginTip => IsApi ? "Change the API address, key and models" : "Sign in from the browser; the link comes to this instance";
    public bool CanUseApi => !Info.IsMain;
    public bool CanClose => IsRunning;

    public void Update(string newLabel, bool running, bool signedIn, string? accountId, int chats, string? api)
    {
        Label = newLabel;
        IsRunning = running;
        IsSignedIn = signedIn;
        Account = accountId is { Length: >= 8 } ? accountId[..8] : "";
        ChatCount = chats;
        ApiSummary = api ?? "";
        IsApi = api != null;
    }
}
