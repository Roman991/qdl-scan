using System.Windows.Input;

namespace NewScan.Services;

/// <summary>ICommand minimale per il binding dei comandi della tray icon.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;

    public RelayCommand(Action execute) => _execute = execute;

    // Lo stato di esecuzione è sempre valido: nessun evento di rivalutazione necessario.
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();
}
