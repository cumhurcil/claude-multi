using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ClaudeMulti.App.Views;

public partial class InputDialog : Window
{
    private readonly Func<string, string?> validate = _ => null;

    public InputDialog()
    {
        InitializeComponent();
    }

    public InputDialog(string title, string label, string initial, Func<string, string?> validate) : this()
    {
        this.validate = validate;
        Title = title;
        Heading.Text = title;
        Prompt.Text = label;
        Input.Text = initial;
        Opened += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }

    public string Value => (Input.Text ?? "").Trim();

    private void Input_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var error = validate(Input.Text ?? "");
        Error.Text = error ?? "";
        Ok.IsEnabled = error == null;
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
