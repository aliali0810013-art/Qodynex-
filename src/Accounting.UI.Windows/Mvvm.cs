using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Accounting.UI.Windows;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
    protected void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _running;
    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) { _execute = execute; _canExecute = canExecute; }
    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);
    public event EventHandler? CanExecuteChanged;
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await _execute().ConfigureAwait(true); }
        finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;
    public RelayCommand(Action execute, Func<bool>? canExecute = null) { _execute = execute; _canExecute = canExecute; }
    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public event EventHandler? CanExecuteChanged;
    public void Execute(object? parameter) => _execute();
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public interface IWorkspaceActionTarget
{
    Task SaveAndPostAsync();
    Task QuickSearchAsync();
    Task RefreshAsync();
    Task OpenSelectedDetailsAsync();
}

public sealed class WorkspaceTab : ObservableObject
{
    public string Key { get; }
    public string Title { get; }
    public object ViewModel { get; }
    public bool HasUnsavedChanges { get; set; }

    public WorkspaceTab(string key, string title, object viewModel)
    {
        Key = key; Title = title; ViewModel = viewModel;
    }
}

public sealed class TabWorkspaceManager : ObservableObject
{
    private WorkspaceTab? _selected;
    public ObservableCollection<WorkspaceTab> Tabs { get; } = new();
    public WorkspaceTab? SelectedTab { get => _selected; set => Set(ref _selected, value); }

    public WorkspaceTab Open(string key, string title, Func<object> factory)
    {
        var existing = Tabs.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { SelectedTab = existing; return existing; }
        var tab = new WorkspaceTab(key, title, factory());
        Tabs.Add(tab); SelectedTab = tab; return tab;
    }

    public bool CloseSelected(Func<WorkspaceTab, bool> confirmDiscard)
    {
        if (SelectedTab is null) return false;
        if (SelectedTab.HasUnsavedChanges && !confirmDiscard(SelectedTab)) return false;
        var index = Tabs.IndexOf(SelectedTab);
        Tabs.Remove(SelectedTab);
        SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Clamp(index - 1, 0, Tabs.Count - 1)];
        return true;
    }
}

public sealed class GlobalKeyboardShortcutHandler
{
    private readonly TabWorkspaceManager _workspace;
    public GlobalKeyboardShortcutHandler(TabWorkspaceManager workspace) => _workspace = workspace;

    private IWorkspaceActionTarget? Active => _workspace.SelectedTab?.ViewModel as IWorkspaceActionTarget;

    public Task HandleAsync(Key key) => key switch
    {
        Key.F2 => Active?.SaveAndPostAsync() ?? Task.CompletedTask,
        Key.F3 => Active?.QuickSearchAsync() ?? Task.CompletedTask,
        Key.F5 => Active?.RefreshAsync() ?? Task.CompletedTask,
        Key.F7 => Active?.OpenSelectedDetailsAsync() ?? Task.CompletedTask,
        _ => Task.CompletedTask
    };
}
