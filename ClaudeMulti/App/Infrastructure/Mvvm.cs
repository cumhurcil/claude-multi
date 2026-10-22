using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;

namespace ClaudeMulti.App.Infrastructure;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    // Avalonia has no CommandManager that re-asks every command after any input, so commands are re-asked on request.
    private static readonly List<WeakReference<RelayCommand>> All = [];

    public RelayCommand(Action execute) : this(_ => execute()) { }

    public RelayCommand(Action execute, Func<bool> canExecute) : this(_ => execute(), _ => canExecute()) { }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);

    /// <summary>Registers the command so <see cref="Requery"/> reaches it.</summary>
    public RelayCommand Tracked()
    {
        lock (All)
        {
            All.RemoveAll(r => !r.TryGetTarget(out _));
            All.Add(new WeakReference<RelayCommand>(this));
        }
        return this;
    }

    /// <summary>Asks every tracked command whether it can run now; call after state that enables or disables buttons changes.</summary>
    public static void Requery()
    {
        List<RelayCommand> live;
        lock (All) live = All.Select(r => r.TryGetTarget(out var c) ? c : null).OfType<RelayCommand>().ToList();
        void Raise() { foreach (var command in live) command.CanExecuteChanged?.Invoke(command, EventArgs.Empty); }
        if (Dispatcher.UIThread.CheckAccess()) Raise(); else Dispatcher.UIThread.Post(Raise);
    }
}

/// <summary>One option of a <see cref="ChoiceGroup"/>, shown as a segment button.</summary>
public sealed class ChoiceItem(ChoiceGroup group, string label, string value) : ObservableObject
{
    public string Label { get; } = label;
    public string Value { get; } = value;

    public bool IsSelected
    {
        get => group.Value == Value;
        set
        {
            if (value) group.Value = Value;
            Raise();
        }
    }

    public void Refresh() => Raise(nameof(IsSelected));
}

/// <summary>A small set of mutually exclusive values (segmented control).</summary>
public sealed class ChoiceGroup : ObservableObject
{
    private string value;

    public ChoiceGroup(string initial, params (string Label, string Value)[] items)
    {
        value = initial;
        foreach (var (label, v) in items) Items.Add(new ChoiceItem(this, label, v));
    }

    public ObservableCollection<ChoiceItem> Items { get; } = [];

    public event Action? Changed;

    public string Value
    {
        get => value;
        set
        {
            if (this.value == value) return;
            this.value = value;
            Raise();
            foreach (var item in Items) item.Refresh();
            Changed?.Invoke();
        }
    }
}
