using Avalonia;
using Avalonia.Controls;

namespace ClaudeMulti.App.Infrastructure;

/// <summary>A settings line: title and description on the left, the control (its content) on the right. Styled in Theme.axaml.</summary>
public sealed class SettingRow : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<SettingRow, string?>(nameof(Title));
    public static readonly StyledProperty<string?> DescriptionProperty = AvaloniaProperty.Register<SettingRow, string?>(nameof(Description));

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Description { get => GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
}
