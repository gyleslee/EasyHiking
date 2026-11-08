using System.Windows.Input;

namespace HikingGuide.App.ViewModels;

public sealed class RelayCommand<T>(Action<T?> execute, Predicate<T?>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter is T value ? value : default) ?? true;
    public void Execute(object? parameter) => execute(parameter is T value ? value : default);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
