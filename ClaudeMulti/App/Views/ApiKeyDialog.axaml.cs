using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ClaudeMulti.Core;

namespace ClaudeMulti.App.Views;

/// <summary>Gateway address, API key and model list for an API-key instance.</summary>
public partial class ApiKeyDialog : Window
{
    private readonly string existingKey = "";
    private bool ready;

    public ApiKeyDialog()
    {
        InitializeComponent();
    }

    public ApiKeyDialog(string title, ApiSettings current) : this()
    {
        Title = title;
        Heading.Text = title;
        existingKey = current.ApiKey;
        BaseUrl.Text = current.BaseUrl;
        Models.Text = string.Join(Environment.NewLine, current.Models);
        WorkDir.Text = current.WorkDir;
        // The saved key is pre-filled (masked) so editing other fields never loses it.
        Key.Text = existingKey;
        ContextTokens.Text = current.ContextTokens > 0 ? current.ContextTokens.ToString() : "";
        SkipPermissions.IsChecked = current.SkipPermissions;
        KeyHint.Text = existingKey.Length > 0 ? $"Saved: {ApiProfile.Mask(existingKey)}" : "E.g. sk-or-v1-…";
        Opened += (_, _) => { ready = true; Key.Focus(); Validate(); };
    }

    public ApiSettings Value => new(
        (BaseUrl.Text ?? "").Trim(),
        (Key.Text ?? "").Trim().Length > 0 ? Key.Text!.Trim() : existingKey,
        (Models.Text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList(),
        (WorkDir.Text ?? "").Trim().Trim('"'),
        ParseContext(),
        SkipPermissions.IsChecked == true);

    /// <summary>Empty = default (0); anything unreadable becomes -1 so validation rejects it.</summary>
    private int ParseContext()
    {
        var text = (ContextTokens.Text ?? "").Trim().Replace(".", "").Replace(",", "").Replace(" ", "");
        if (text.Length == 0) return 0;
        return int.TryParse(text, out var value) ? value : -1;
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder Claude Code will work in", AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) WorkDir.Text = path;
    }

    private void Validate()
    {
        var error = ApiProfile.Validate(Value);
        Error.Text = error ?? "";
        Ok.IsEnabled = error == null;
    }

    private void Field_Changed(object? sender, TextChangedEventArgs e)
    {
        if (ready) Validate();
    }

    private void Field_Toggled(object? sender, RoutedEventArgs e)
    {
        if (ready) Validate();
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
