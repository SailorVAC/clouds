using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DC.App;

internal sealed class TaskRow(int taskId) : INotifyPropertyChanged
{
    private string _nonce = "—";
    private string _status = "Pending";

    public int TaskId { get; } = taskId;
    public string Nonce
    {
        get => _nonce;
        set { _nonce = value; Notify(); }
    }
    public string Status
    {
        get => _status;
        set { _status = value; Notify(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
