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
        _ = ResolveLocationAsync();
    }

    public string Id => _connection.Id;
    public string Network => _connection.Metadata.Network;
    public string Type => _connection.Metadata.Type;
    public string Host => string.IsNullOrWhiteSpace(_connection.Metadata.Host) 
        ? _connection.Metadata.DestinationIP 
        : _connection.Metadata.Host;

    public string SiteName => GetSiteName(Host);
    
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
    
    // GeoIP Information
    private double _latitude;
    private double _longitude;
    private string? _country;

    public double Latitude
    {
        get => _latitude;
        set { if (_latitude != value) { _latitude = value; OnPropertyChanged(); } }
    }

    public double Longitude
    {
        get => _longitude;
        set { if (_longitude != value) { _longitude = value; OnPropertyChanged(); } }
    }

    public string? Country
    {
        get => _country;
        set { if (_country != value) { _country = value; OnPropertyChanged(); } }
    }

    private async Task ResolveLocationAsync()
    {
        // Try to resolve the destination IP
        var host = _connection.Metadata.DestinationIP;
        if (string.IsNullOrWhiteSpace(host))
        {
            host = _connection.Metadata.Host; // Fallback to host if IP is empty, though DNS resolution is better
        }
        
        var geoInfo = await WowProxy.App.Services.GeoIpResolutionService.Instance.ResolveIpAsync(host);
        if (geoInfo != null && geoInfo.IsResolved)
        {
            Latitude = geoInfo.Latitude;
            Longitude = geoInfo.Longitude;
            Country = geoInfo.Country;
        }
    }

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

    private static string GetSiteName(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return "Unknown";
        if (System.Net.IPAddress.TryParse(host, out _)) return host;

        var lowerHost = host.ToLowerInvariant();

        // 1. Common CDN/Service Mappings
        if (IsDomain(lowerHost, "googlevideo.com", "ytimg.com", "ggpht.com", "youtube.com"))
            return "YouTube";
        if (IsDomain(lowerHost, "fbcdn.net", "facebook.com", "messenger.com"))
            return "Facebook";
        if (IsDomain(lowerHost, "netflix.com", "nflxext.com", "nflxvideo.net", "nflxso.net"))
            return "Netflix";
        if (IsDomain(lowerHost, "twimg.com", "twitter.com", "t.co", "x.com"))
            return "Twitter/X";
        if (IsDomain(lowerHost, "discordapp.net", "discord.com", "discord.gg"))
            return "Discord";
        if (IsDomain(lowerHost, "githubusercontent.com", "github.com"))
            return "GitHub";
        if (IsDomain(lowerHost, "microsoft.com", "bing.com", "live.com", "windowsupdate.com", "outlook.com"))
            return "Microsoft";
        if (IsDomain(lowerHost, "apple.com", "icloud.com", "mzstatic.com"))
            return "Apple";
        if (IsDomain(lowerHost, "steamcontent.com", "steampowered.com"))
            return "Steam";
        if (IsDomain(lowerHost, "akamaized.net", "akamaihd.net", "fastly.net", "cloudfront.net"))
            return "CDN/Static";

        // 2. Fallback: Simple Root Domain Extraction
        var parts = lowerHost.Split('.');
        if (parts.Length >= 2)
        {
            // Handle common double extensions like .com.cn, .net.cn, .org.cn
            if (parts.Length >= 3 && (parts[parts.Length - 2] == "com" || parts[parts.Length - 2] == "net" || parts[parts.Length - 2] == "org") && parts[parts.Length - 1].Length == 2)
            {
                return $"{parts[parts.Length - 3]}.{parts[parts.Length - 2]}.{parts[parts.Length - 1]}";
            }
            return $"{parts[parts.Length - 2]}.{parts[parts.Length - 1]}";
        }

        return host;
    }

    private static bool IsDomain(string host, params string[] domains)
    {
        foreach (var domain in domains)
        {
            if (host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
