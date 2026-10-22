using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace ClaudeMulti.App.Infrastructure;

/// <summary>A rounded horizontal bar filled from the left, for the cache countdown.</summary>
public sealed class LifeBar : Control
{
    private static readonly IBrush Track = new ImmutableSolidColorBrush(Color.FromRgb(0x30, 0x2B, 0x28));

    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<LifeBar, double>(nameof(Value));
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<LifeBar, IBrush?>(nameof(Fill), Brushes.Gray);

    static LifeBar() => AffectsRender<LifeBar>(ValueProperty, FillProperty);

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var r = h / 2;
        context.DrawRectangle(Track, null, new RoundedRect(new Rect(0, 0, w, h), r));
        var filled = Math.Clamp(Value, 0, 1) * w;
        if (filled < 1) return;
        using (context.PushClip(new RoundedRect(new Rect(0, 0, w, h), r)))
            context.DrawRectangle(Fill, null, new RoundedRect(new Rect(0, 0, Math.Max(filled, h), h), r));
    }
}

/// <summary>Read / written / new tokens of the last request as one stacked bar.</summary>
public sealed class StackedBar : Control
{
    private static readonly IBrush Track = new ImmutableSolidColorBrush(Color.FromRgb(0x30, 0x2B, 0x28));
    private static readonly IBrush ReadBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x5D, 0xBB, 0x84));
    private static readonly IBrush WriteBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xE8, 0xB0, 0x4A));
    private static readonly IBrush FreshBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xE5, 0x60, 0x4E));

    public static readonly StyledProperty<double> ReadProperty = AvaloniaProperty.Register<StackedBar, double>(nameof(Read));
    public static readonly StyledProperty<double> WriteProperty = AvaloniaProperty.Register<StackedBar, double>(nameof(Write));
    public static readonly StyledProperty<double> FreshProperty = AvaloniaProperty.Register<StackedBar, double>(nameof(Fresh));

    static StackedBar() => AffectsRender<StackedBar>(ReadProperty, WriteProperty, FreshProperty);

    public double Read { get => GetValue(ReadProperty); set => SetValue(ReadProperty, value); }
    public double Write { get => GetValue(WriteProperty); set => SetValue(WriteProperty, value); }
    public double Fresh { get => GetValue(FreshProperty); set => SetValue(FreshProperty, value); }

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height, r = h / 2;
        context.DrawRectangle(Track, null, new RoundedRect(new Rect(0, 0, w, h), r));
        var total = Read + Write + Fresh;
        if (total <= 0) return;

        using (context.PushClip(new RoundedRect(new Rect(0, 0, w, h), r)))
        {
            var x = 0.0;
            foreach (var (value, brush) in new[] { (Read, ReadBrush), (Write, WriteBrush), (Fresh, FreshBrush) })
            {
                var width = value / total * w;
                if (width <= 0) continue;
                context.DrawRectangle(brush, null, new Rect(x, 0, width, h));
                x += width;
            }
        }
    }
}
