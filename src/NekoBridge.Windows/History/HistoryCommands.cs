using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace NekoBridge.Windows.History;

internal abstract class ObservableModel:INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName]string? name=null)=>PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name));
}
internal sealed class HistoryCommand(Func<object?,Task> execute,Action<Exception> error,Func<object?,bool>? canExecute=null):ICommand
{
    private bool _busy;
    public event EventHandler? CanExecuteChanged;
    internal void Notify()=>CanExecuteChanged?.Invoke(this,EventArgs.Empty);
    public bool CanExecute(object? parameter)=>!_busy&&(canExecute?.Invoke(parameter)??true);
    public async void Execute(object? parameter)
    {
        if(!CanExecute(parameter))return;
        _busy=true;CanExecuteChanged?.Invoke(this,EventArgs.Empty);
        try{await execute(parameter);}catch(OperationCanceledException){}catch(Exception ex){error(ex);}
        finally{_busy=false;CanExecuteChanged?.Invoke(this,EventArgs.Empty);}
    }
}
