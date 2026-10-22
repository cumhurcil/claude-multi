using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ClaudeMulti.App.Views;

/// <summary>A message with OK, or a question with Yes / No. Closes with true for OK and Yes.</summary>
public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
    }

    public MessageDialog(string title, string message, bool confirm) : this()
    {
        Title = title;
        Heading.Text = title;
        Body.Text = message;
        No.IsVisible = confirm;
        Yes.Content = confirm ? "Yes" : "OK";
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close(false);
        base.OnKeyDown(e);
    }

    private void Yes_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void No_Click(object? sender, RoutedEventArgs e) => Close(false);
}
