using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WowProxy.Infrastructure.Mitm;

namespace WowProxy.App.ViewModels;

/// <summary>
/// ViewModel for the MITM packet capture tab.
/// Manages the MITM proxy lifecycle, domain filters, and captured message display.
/// </summary>
public class MitmCaptureViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly MitmCertificateAuthority _ca;
    private MitmProxyServer? _server;
    private bool _isRunning;
    private int _mitmPort = 10811;
    private string _mitmPortText = "10811";
    private string _filterDomainsText = "api.openai.com\napi.anthropic.com\napi.cursor.com\napi-inference.huggingface.co\ncodestory.ai\nwindsurf.ai\napi.githubcopilot.com\ncopilot-proxy.githubusercontent.com";
    private CapturedHttpMessage? _selectedMessage;
    private string _statusText = "未启动";
    private string _searchText = "";
    private bool _caInstalled;

    public MitmCaptureViewModel()
    {
        _ca = new MitmCertificateAuthority();
        CapturedMessages = new ObservableCollection<CapturedHttpMessage>();

        StartCommand = new RelayCommand(_ => ToggleStartStop());
        StopCommand = new RelayCommand(_ => Stop());
        ClearCommand = new RelayCommand(_ => Clear());
        InstallCaCommand = new RelayCommand(_ => InstallCa());
        OpenCaCertCommand = new RelayCommand(_ => OpenCaCertFolder());
        CopyRequestBodyCommand = new RelayCommand(_ => CopyRequestBody());
        CopyResponseBodyCommand = new RelayCommand(_ => CopyResponseBody());

        _caInstalled = _ca.IsRootCaInstalled();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand InstallCaCommand { get; }
    public RelayCommand OpenCaCertCommand { get; }
    public RelayCommand CopyRequestBodyCommand { get; }
    public RelayCommand CopyResponseBodyCommand { get; }

    public ObservableCollection<CapturedHttpMessage> CapturedMessages { get; }

    public bool IsRunning
    {
        get => _isRunning;
        set { if (_isRunning != value) { _isRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(ToggleButtonText)); } }
    }

    public string ToggleButtonText => IsRunning ? "停止抓包" : "开始抓包";

    public string MitmPortText
    {
        get => _mitmPortText;
        set { if (_mitmPortText != value) { _mitmPortText = value; OnPropertyChanged(); } }
    }

    public string FilterDomainsText
    {
        get => _filterDomainsText;
        set { if (_filterDomainsText != value) { _filterDomainsText = value; OnPropertyChanged(); } }
    }

    public CapturedHttpMessage? SelectedMessage
    {
        get => _selectedMessage;
        set
        {
            if (_selectedMessage != value)
            {
                _selectedMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedRequestHeaders));
                OnPropertyChanged(nameof(SelectedRequestBody));
                OnPropertyChanged(nameof(SelectedResponseHeaders));
                OnPropertyChanged(nameof(SelectedResponseBody));
                OnPropertyChanged(nameof(SelectedSummary));
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    public bool HasSelection => _selectedMessage != null;

    public string SelectedSummary
    {
        get
        {
            if (_selectedMessage == null) return "";
            var msg = _selectedMessage;
            return $"{msg.Method} {msg.Url}\n状态码: {msg.StatusCode}  耗时: {msg.DurationMs}ms" +
                   (msg.Error != null ? $"\n错误: {msg.Error}" : "");
        }
    }

    public string SelectedRequestHeaders
    {
        get
        {
            if (_selectedMessage == null) return "";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{_selectedMessage.Method} {_selectedMessage.Url}");
            sb.AppendLine();
            foreach (var (k, v) in _selectedMessage.RequestHeaders)
                sb.AppendLine($"{k}: {v}");
            return sb.ToString();
        }
    }

    public string SelectedRequestBody
    {
        get
        {
            if (_selectedMessage == null) return "";
            var body = _selectedMessage.RequestBody;
            return TryFormatJson(body);
        }
    }

    public string SelectedResponseHeaders
    {
        get
        {
            if (_selectedMessage == null) return "";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"HTTP {_selectedMessage.StatusCode}");
            sb.AppendLine();
            foreach (var (k, v) in _selectedMessage.ResponseHeaders)
                sb.AppendLine($"{k}: {v}");
            return sb.ToString();
        }
    }

    public string SelectedResponseBody
    {
        get
        {
            if (_selectedMessage == null) return "";
            var body = _selectedMessage.ResponseBody;
            return TryFormatJson(body);
        }
    }

    public string StatusText
    {
        get => _statusText;
        set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
    }

    public string SearchText
    {
        get => _searchText;
        set { if (_searchText != value) { _searchText = value; OnPropertyChanged(); } }
    }

    public bool CaInstalled
    {
        get => _caInstalled;
        set { if (_caInstalled != value) { _caInstalled = value; OnPropertyChanged(); OnPropertyChanged(nameof(CaStatusText)); } }
    }

    public string CaStatusText => CaInstalled ? "CA 证书已安装" : "CA 证书未安装（HTTPS 抓包需要安装）";

    // The port for external configuration (IDE should use this as proxy)
    public int ActivePort => _mitmPort;

    private void Start()
    {
        if (IsRunning) return;

        if (!int.TryParse(MitmPortText, out var port) || port < 1 || port > 65535)
        {
            StatusText = "端口无效";
            return;
        }

        _mitmPort = port;

        var domains = FilterDomainsText
            .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .ToList();

        try
        {
            _server = new MitmProxyServer(_ca);
            _server.SetInterceptDomains(domains);
            _server.OnMessageCaptured += OnMessageCapturedCallback;
            _server.OnLog += OnLogCallback;
            _server.Start(port);

            IsRunning = true;
            StatusText = $"MITM 抓包运行中 - 127.0.0.1:{port}";
        }
        catch (Exception ex)
        {
            StatusText = $"启动失败: {ex.Message}";
        }
    }

    private void ToggleStartStop()
    {
        if (IsRunning) Stop();
        else Start();
    }

    private void Stop()
    {
        if (!IsRunning || _server == null) return;

        _server.OnMessageCaptured -= OnMessageCapturedCallback;
        _server.OnLog -= OnLogCallback;
        _ = _server.StopAsync();
        _server = null;

        IsRunning = false;
        StatusText = "已停止";
    }

    /// <summary>
    /// Set the upstream proxy for the MITM server (call when sing-box starts).
    /// </summary>
    public void SetUpstreamProxy(int singBoxMixedPort)
    {
        _server?.SetUpstreamProxy("127.0.0.1", singBoxMixedPort);
    }

    private void Clear()
    {
        CapturedMessages.Clear();
        _server?.ClearCaptured();
        SelectedMessage = null;
    }

    private void InstallCa()
    {
        try
        {
            var ok = _ca.TryInstallRootCa();
            CaInstalled = _ca.IsRootCaInstalled();
            StatusText = ok ? "CA 证书安装成功！" : "CA 证书安装失败，请手动安装";
        }
        catch (Exception ex)
        {
            StatusText = $"安装 CA 证书失败: {ex.Message}";
        }
    }

    private void OpenCaCertFolder()
    {
        try
        {
            _ca.GetOrCreateRootCa(); // ensure created
            var dir = System.IO.Path.GetDirectoryName(_ca.CaCertPath);
            if (dir != null)
                System.Diagnostics.Process.Start("explorer.exe", dir);
        }
        catch { }
    }

    private void CopyRequestBody()
    {
        if (_selectedMessage == null) return;
        try { System.Windows.Clipboard.SetText(_selectedMessage.RequestBody); StatusText = "Request Body 已复制"; }
        catch { }
    }

    private void CopyResponseBody()
    {
        if (_selectedMessage == null) return;
        try { System.Windows.Clipboard.SetText(_selectedMessage.ResponseBody); StatusText = "Response Body 已复制"; }
        catch { }
    }

    private void OnMessageCapturedCallback(CapturedHttpMessage msg)
    {
        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            // Apply search filter
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                var s = SearchText;
                if (!msg.Url.Contains(s, StringComparison.OrdinalIgnoreCase) &&
                    !msg.Host.Contains(s, StringComparison.OrdinalIgnoreCase) &&
                    !msg.RequestBody.Contains(s, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            CapturedMessages.Insert(0, msg);
            while (CapturedMessages.Count > 500)
                CapturedMessages.RemoveAt(CapturedMessages.Count - 1);
        });
    }

    private void OnLogCallback(string msg)
    {
        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            StatusText = msg;
        });
    }

    private static string TryFormatJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(trimmed);
                return System.Text.Json.JsonSerializer.Serialize(doc, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            }
            catch { }
        }
        return text;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        Stop();
        _ca.Dispose();
    }
}
