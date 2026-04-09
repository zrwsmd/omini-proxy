using System.ComponentModel;
using System.Runtime.CompilerServices;
using WowProxy.Domain;

namespace WowProxy.App.Models;

public class ProxyNodeModel : INotifyPropertyChanged
{
    private int? _latency;
    private double? _speed;
    private bool _isActive;

    private ProxyNode _node;

    public ProxyNodeModel(ProxyNode node)
    {
        _node = node;
    }

    public ProxyNode Node
    {
        get => _node;
        set
        {
            _node = value;
            OnPropertyChanged(null); // Notify that all properties from Node might have changed
        }
    }

    // Expose Node properties for DataGrid binding
    public string Id => Node.Id;
    public ProxyNodeType Type => Node.Type;
    public string Name => Node.Name;
    public string Server => Node.Server;
    public int Port => Node.Port;
    public string? TransportType => Node.TransportType;
    public bool TlsEnabled => Node.TlsEnabled;

    // Mutable properties for UI
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive != value)
            {
                _isActive = value;
                OnPropertyChanged();
            }
        }
    }

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
            var changed = _speed.HasValue != value.HasValue
                || (_speed.HasValue && value.HasValue && Math.Abs(_speed.Value - value.Value) > 0.01);

            if (changed)
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
