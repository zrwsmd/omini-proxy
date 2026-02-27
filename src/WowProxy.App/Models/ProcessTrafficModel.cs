using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WowProxy.App.Models;

public class ProcessTrafficModel : INotifyPropertyChanged
{
    private string _processName = string.Empty;
    private long _cumulativeUpload;
    private long _cumulativeDownload;
    private long _currentUploadSpeed;
    private long _currentDownloadSpeed;
    private int _activeConnections;
    private string _mainRule = "Unknown";
    private string _processPath = string.Empty;

    public string ProcessName
    {
        get => _processName;
        set { if (_processName != value) { _processName = value; OnPropertyChanged(); } }
    }

    public string ProcessPath
    {
        get => _processPath;
        set { if (_processPath != value) { _processPath = value; OnPropertyChanged(); } }
    }

    public long CumulativeUpload
    {
        get => _cumulativeUpload;
        set 
        { 
            if (_cumulativeUpload != value) 
            { 
                _cumulativeUpload = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(CumulativeUploadText)); 
            } 
        }
    }

    public long CumulativeDownload
    {
        get => _cumulativeDownload;
        set 
        { 
            if (_cumulativeDownload != value) 
            { 
                _cumulativeDownload = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(CumulativeDownloadText)); 
            } 
        }
    }

    public long CurrentUploadSpeed
    {
        get => _currentUploadSpeed;
        set 
        { 
            if (_currentUploadSpeed != value) 
            { 
                _currentUploadSpeed = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(CurrentUploadSpeedText)); 
            } 
        }
    }

    public long CurrentDownloadSpeed
    {
        get => _currentDownloadSpeed;
        set 
        { 
            if (_currentDownloadSpeed != value) 
            { 
                _currentDownloadSpeed = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(CurrentDownloadSpeedText)); 
            } 
        }
    }

    public int ActiveConnections
    {
        get => _activeConnections;
        set { if (_activeConnections != value) { _activeConnections = value; OnPropertyChanged(); } }
    }

    public string MainRule
    {
        get => _mainRule;
        set { if (_mainRule != value) { _mainRule = value; OnPropertyChanged(); } }
    }

    public string CumulativeUploadText => ToHumanReadable(CumulativeUpload);
    public string CumulativeDownloadText => ToHumanReadable(CumulativeDownload);
    public string CurrentUploadSpeedText => ToHumanReadable(CurrentUploadSpeed) + "/s";
    public string CurrentDownloadSpeedText => ToHumanReadable(CurrentDownloadSpeed) + "/s";

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
