using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Obsidian.Common;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void Raise(params string[] names)
    {
        foreach (string name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Small dispatcher helper shared by the command implementations. Commands can
/// be invalidated from worker threads, so every notification is marshalled to
/// the UI thread when one exists; when there is no application (unit-style
/// hosts, designer) the callback runs inline instead of being dropped.
/// </summary>
internal static class UiDispatch
{
    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return;

        dispatcher.BeginInvoke(action, DispatcherPriority.DataBind);
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged()
        => UiDispatch.Run(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
}

/// <summary>
/// Async <see cref="ICommand"/> wrapper. <see cref="ICommand.Execute"/> is
/// necessarily <c>async void</c>; exceptions are therefore caught here and
/// reported through <see cref="ExceptionHandler"/> (installed by the
/// application) instead of crashing the dispatcher.
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _isRunning;

    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute())
    {
    }

    /// <summary>
    /// Raised for faults the command handler did not handle itself. The
    /// application installs a handler that logs and shows a toast.
    /// </summary>
    public static Action<Exception>? ExceptionHandler { get; set; }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => _isRunning;

    public bool CanExecute(object? parameter)
        => !_isRunning && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
            return;

        _isRunning = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute(parameter).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a normal outcome for cancellable commands.
        }
        catch (Exception exception)
        {
            ReportException(exception);
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged()
        => UiDispatch.Run(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));

    public static void ReportException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            Action<Exception>? handler = ExceptionHandler;
            if (handler != null)
                handler(exception);
            else
                Trace.TraceError("Unhandled command exception: " + exception);
        }
        catch
        {
            // A broken reporter must never take the process down.
        }
    }
}
