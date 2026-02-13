using System.ComponentModel;
using System.Runtime.CompilerServices;
using WowProxy.Domain;

namespace WowProxy.App.Models;

public class ProxyNodeModel : INotifyPropertyChanged
{
    private int? _latency;
    private double? _speed;

    public ProxyNodeModel(ProxyNode node)
    {
        Node = node;
    }

    public ProxyNode Node { get; }

    // Expose Node properties for DataGrid binding
    public string Id => Node.Id;
    public ProxyNodeType Type => Node.Type;
    public string Name => Node.Name;
    public string Server => Node.Server;
    public int Port => Node.Port;
    public string? TransportType => Node.TransportType;
    public bool TlsEnabled => Node.TlsEnabled;

    // Mutable properties for UI
    public int? Latency
    {
        get => _latency;
        set
        {
            if (_latency != value)
            {
                _latency = value;
                OnPropertyChanged();
            }
        }
    }

    public double? Speed
    {
        get => _speed;
        set
        {
            if (Math.Abs((_speed ?? 0) - (value ?? 0)) > 0.01)
            {
                _speed = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
