using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace M30TestApp.Wpf.Mvvm;

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : new Predicate<object?>(_ => canExecute()))
    { }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);

    public event EventHandler? CanExecuteChanged
    {
        add { CommandManager.RequerySuggested += value; }
        remove { CommandManager.RequerySuggested -= value; }
    }

    public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _running;

    /// <summary>
    /// Optional global error sink. Invoked on the UI thread when an async command
    /// throws. Defaults to logging + a non-blocking MessageBox so SIM/HW failures
    /// never bring the app down.
    /// </summary>
    public static Action<Exception, string> ErrorHandler { get; set; } =
        (ex, source) =>
        {
            M30TestApp.Core.Common.AppLog.Error(source, ex.ToString());
            try
            {
                System.Windows.MessageBox.Show(
                    ex.Message,
                    $"{source} 操作失败",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
            catch { /* ignore secondary failures from MessageBox */ }
        };

    public string Source { get; init; } = "Command";

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await _execute().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Treat user-initiated cancellation as a normal exit, no popup.
            M30TestApp.Core.Common.AppLog.Info(Source, "操作已取消");
        }
        catch (Exception ex)
        {
            // Any other failure: surface to the user instead of crashing the app.
            try { ErrorHandler(ex, Source); }
            catch { /* swallow to guarantee the command never tears down the dispatcher */ }
        }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public event EventHandler? CanExecuteChanged
    {
        add { CommandManager.RequerySuggested += value; }
        remove { CommandManager.RequerySuggested -= value; }
    }
}
