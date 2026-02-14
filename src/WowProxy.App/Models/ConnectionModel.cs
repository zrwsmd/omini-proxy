using System.ComponentModel;
using System.Runtime.CompilerServices;
using WowProxy.Core.Abstractions.Models.Clash;

namespace WowProxy.App.Models;

public class ConnectionModel : INotifyPropertyChanged
{
    private readonly Connection _connection;
    private long _uploadSpeed;
    private long _downloadSpeed;

    public ConnectionModel(Connection connection)
    {
        _connection = connection;
        LastUpload = connection.Upload;
        LastDownload = connection.Download;
    }

    public string Id => _connection.Id;
    public string Network => _connection.Metadata.Network;
    public string Type => _connection.Metadata.Type;
    public string Host => string.IsNullOrWhiteSpace(_connection.Metadata.Host) 
        ? _connection.Metadata.DestinationIP 
        : _connection.Metadata.Host;
    
    public string DestinationPort => _connection.Metadata.DestinationPort;
    
    public string Process => !string.IsNullOrWhiteSpace(_connection.Metadata.Process)
        ? _connection.Metadata.Process
        : System.IO.Path.GetFileName(_connection.Metadata.ProcessPath);

    public string Chains => string.Join(" -> ", _connection.Chains.AsEnumerable().Reverse().Take(2).Reverse());
    public string Rule => _connection.Rule;
    public DateTime Start => _connection.Start;

    public long UploadTotal => _connection.Upload;
    public long DownloadTotal => _connection.Download;

    public long LastUpload { get; private set; }
    public long LastDownload { get; private set; }

    public long UploadSpeed
    {
        get => _uploadSpeed;
        set
        {
            if (_uploadSpeed != value)
            {
                _uploadSpeed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(UploadSpeedText));
            }
        }
    }

    public long DownloadSpeed
    {
        get => _downloadSpeed;
        set
        {
            if (_downloadSpeed != value)
            {
                _downloadSpeed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DownloadSpeedText));
            }
        }
    }

    public string UploadTotalText => ToHumanReadable(UploadTotal);
    public string DownloadTotalText => ToHumanReadable(DownloadTotal);
    public string UploadSpeedText => ToHumanReadable(UploadSpeed) + "/s";
    public string DownloadSpeedText => ToHumanReadable(DownloadSpeed) + "/s";

    public void Update(Connection newConnection)
    {
        // Update totals
        _connection.Upload = newConnection.Upload;
        _connection.Download = newConnection.Download;
        OnPropertyChanged(nameof(UploadTotal));
        OnPropertyChanged(nameof(DownloadTotal));
        OnPropertyChanged(nameof(UploadTotalText));
        OnPropertyChanged(nameof(DownloadTotalText));

        // Calculate speed (bytes per second, assuming update interval is 1s)
        UploadSpeed = _connection.Upload - LastUpload;
        DownloadSpeed = _connection.Download - LastDownload;

        LastUpload = _connection.Upload;
        LastDownload = _connection.Download;
    }

    private static string ToHumanReadable(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
