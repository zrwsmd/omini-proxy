using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using WowProxy.Infrastructure.Mitm;

namespace WowProxy.App.ViewModels;

/// <summary>
/// ViewModel for the MITM packet capture tab.
/// Captures ALL traffic. Post-capture display filters (like Wireshark) let users
/// narrow down by field, operator, value, and regex.
/// Works standalone — no sing-box connection required.
/// </summary>
public class MitmCaptureViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly MitmCertificateAuthority _ca;
    private MitmProxyServer? _server;
    private bool _isRunning;
    private int _mitmPort = 10811;
    private string _mitmPortText = "10811";
    private CapturedHttpMessage? _selectedMessage;
    private string _statusText = "就绪 — 点击「开始抓包」捕获所有流量，然后用显示过滤器筛选";
    private bool _caInstalled;

    // All captured messages (unfiltered)
    private readonly List<CapturedHttpMessage> _allMessages = new();
    private readonly object _allMessagesLock = new();

    // Display filter
    private string _filterField = "all";
    private string _filterOperator = "contains";
    private string _filterValue = "";
    private bool _filterRegex;
    private int _capturedCount;
    private int _displayedCount;

    public MitmCaptureViewModel()
    {
        _ca = new MitmCertificateAuthority();
        CapturedMessages = new ObservableCollection<CapturedHttpMessage>();
        FilterFields = new ObservableCollection<string>(CapturedHttpMessage.FilterFieldNames);
        FilterOperators = new ObservableCollection<string>(new[]
        {
            "contains", "not contains", "equals", "not equals",
            "starts with", "ends with", "regex", ">", "<"
        });

        StartCommand = new RelayCommand(_ => ToggleStartStop());
        ClearCommand = new RelayCommand(_ => Clear());
        InstallCaCommand = new RelayCommand(_ => InstallCa());
        OpenCaCertCommand = new RelayCommand(_ => OpenCaCertFolder());
        CopyRequestBodyCommand = new RelayCommand(_ => CopyRequestBody());
        CopyResponseBodyCommand = new RelayCommand(_ => CopyResponseBody());
        ApplyFilterCommand = new RelayCommand(_ => ApplyFilter());
        ClearFilterCommand = new RelayCommand(_ => ClearFilter());

        _caInstalled = _ca.IsRootCaInstalled();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public RelayCommand StartCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand InstallCaCommand { get; }
    public RelayCommand OpenCaCertCommand { get; }
    public RelayCommand CopyRequestBodyCommand { get; }
    public RelayCommand CopyResponseBodyCommand { get; }
    public RelayCommand ApplyFilterCommand { get; }
    public RelayCommand ClearFilterCommand { get; }

    public ObservableCollection<CapturedHttpMessage> CapturedMessages { get; }
    public ObservableCollection<string> FilterFields { get; }
    public ObservableCollection<string> FilterOperators { get; }

    public bool IsRunning
    {
        get => _isRunning;
        set { if (_isRunning != value) { _isRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(ToggleButtonText)); } }
    }

    public string ToggleButtonText => IsRunning ? "■ 停止抓包" : "▶ 开始抓包";

    public string MitmPortText
    {
        get => _mitmPortText;
        set { if (_mitmPortText != value) { _mitmPortText = value; OnPropertyChanged(); } }
    }

    // Display filter properties
    public string FilterField
    {
        get => _filterField;
        set { if (_filterField != value) { _filterField = value; OnPropertyChanged(); } }
    }

    public string FilterOperator
    {
        get => _filterOperator;
        set { if (_filterOperator != value) { _filterOperator = value; OnPropertyChanged(); } }
    }

    public string FilterValue
    {
        get => _filterValue;
        set { if (_filterValue != value) { _filterValue = value; OnPropertyChanged(); } }
    }

    public bool FilterRegex
    {
        get => _filterRegex;
        set { if (_filterRegex != value) { _filterRegex = value; OnPropertyChanged(); } }
    }

    public int CapturedCount
    {
        get => _capturedCount;
        set { if (_capturedCount != value) { _capturedCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(CountText)); } }
    }

    public int DisplayedCount
    {
        get => _displayedCount;
        set { if (_displayedCount != value) { _displayedCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(CountText)); } }
    }

    public string CountText => $"捕获: {CapturedCount}  显示: {DisplayedCount}";

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
            return $"[{msg.Protocol}] {msg.Method} {msg.Url}\n" +
                   $"Host: {msg.Host}  IP: {msg.RemoteAddress}:{msg.RemotePort}\n" +
                   $"状态码: {msg.StatusCode}  耗时: {msg.DurationMs}ms  " +
                   $"请求大小: {msg.RequestSize}B  响应大小: {msg.ResponseSize}B" +
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
            sb.AppendLine($"Host: {_selectedMessage.Host}");
            sb.AppendLine($"Protocol: {_selectedMessage.Protocol}");
            sb.AppendLine($"Remote: {_selectedMessage.RemoteAddress}:{_selectedMessage.RemotePort}");
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
            return TryFormatJson(_selectedMessage.RequestBody);
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
            return TryFormatJson(_selectedMessage.ResponseBody);
        }
    }

    public string StatusText
    {
        get => _statusText;
        set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
    }

    public bool CaInstalled
    {
        get => _caInstalled;
        set { if (_caInstalled != value) { _caInstalled = value; OnPropertyChanged(); OnPropertyChanged(nameof(CaStatusText)); } }
    }

    public string CaStatusText => CaInstalled ? "✓ CA 已安装" : "✗ CA 未安装（HTTPS 抓包需先安装）";

    public int ActivePort => _mitmPort;

    #region Start / Stop

    private void ToggleStartStop()
    {
        if (IsRunning) Stop();
        else Start();
    }

    private void Start()
    {
        if (IsRunning) return;

        if (!int.TryParse(MitmPortText, out var port) || port < 1 || port > 65535)
        {
            StatusText = "端口无效（1-65535）";
            return;
        }

        _mitmPort = port;

        try
        {
            _server = new MitmProxyServer(_ca);
            // No domain filter — capture everything
            _server.OnMessageCaptured += OnMessageCapturedCallback;
            _server.OnLog += OnLogCallback;
            _server.Start(port);

            IsRunning = true;
            StatusText = $"抓包运行中 — 代理地址 127.0.0.1:{port}（捕获全部流量，用过滤器筛选）";
        }
        catch (Exception ex)
        {
            StatusText = $"启动失败: {ex.Message}";
        }
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

    public void SetUpstreamProxy(int singBoxMixedPort)
    {
        _server?.SetUpstreamProxy("127.0.0.1", singBoxMixedPort);
    }

    public void ClearUpstreamProxy()
    {
        _server?.ClearUpstreamProxy();
    }

    #endregion

    #region Filter

    private void ApplyFilter()
    {
        RebuildDisplayList();
        var activeFilter = string.IsNullOrWhiteSpace(FilterValue)
            ? "无过滤"
            : $"过滤: {FilterField} {FilterOperator} \"{FilterValue}\"{(FilterRegex ? " (regex)" : "")}";
        StatusText = $"{activeFilter} — {CountText}";
    }

    private void ClearFilter()
    {
        FilterValue = "";
        FilterField = "all";
        FilterOperator = "contains";
        FilterRegex = false;
        RebuildDisplayList();
        StatusText = $"过滤已清除 — {CountText}";
    }

    private void RebuildDisplayList()
    {
        CapturedMessages.Clear();
        SelectedMessage = null;

        List<CapturedHttpMessage> snapshot;
        lock (_allMessagesLock) { snapshot = _allMessages.ToList(); }

        foreach (var msg in snapshot)
        {
            if (MatchesFilter(msg))
                CapturedMessages.Add(msg);
        }

        DisplayedCount = CapturedMessages.Count;
    }

    private bool MatchesFilter(CapturedHttpMessage msg)
    {
        if (string.IsNullOrWhiteSpace(FilterValue)) return true;

        var fieldValue = msg.GetField(FilterField);
        var filterVal = FilterValue;

        // If regex mode is on, use regex regardless of operator
        if (FilterRegex || FilterOperator == "regex")
        {
            try
            {
                return Regex.IsMatch(fieldValue, filterVal, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            }
            catch { return false; }
        }

        return FilterOperator switch
        {
            "contains" => fieldValue.Contains(filterVal, StringComparison.OrdinalIgnoreCase),
            "not contains" => !fieldValue.Contains(filterVal, StringComparison.OrdinalIgnoreCase),
            "equals" => fieldValue.Equals(filterVal, StringComparison.OrdinalIgnoreCase),
            "not equals" => !fieldValue.Equals(filterVal, StringComparison.OrdinalIgnoreCase),
            "starts with" => fieldValue.StartsWith(filterVal, StringComparison.OrdinalIgnoreCase),
            "ends with" => fieldValue.EndsWith(filterVal, StringComparison.OrdinalIgnoreCase),
            ">" => double.TryParse(fieldValue, out var a) && double.TryParse(filterVal, out var b) && a > b,
            "<" => double.TryParse(fieldValue, out var c) && double.TryParse(filterVal, out var d) && c < d,
            _ => fieldValue.Contains(filterVal, StringComparison.OrdinalIgnoreCase),
        };
    }

    #endregion

    #region Actions

    private void Clear()
    {
        lock (_allMessagesLock) { _allMessages.Clear(); }
        CapturedMessages.Clear();
        _server?.ClearCaptured();
        SelectedMessage = null;
        CapturedCount = 0;
        DisplayedCount = 0;
        StatusText = "已清空";
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
            _ca.GetOrCreateRootCa();
            var dir = System.IO.Path.GetDirectoryName(_ca.CaCertPath);
            if (dir != null)
                System.Diagnostics.Process.Start("explorer.exe", dir);
        }
        catch { }
    }

    private void CopyRequestBody()
    {
        if (_selectedMessage == null) return;
        try { System.Windows.Clipboard.SetText(_selectedMessage.RequestBody); StatusText = "Request Body 已复制到剪贴板"; }
        catch { }
    }

    private void CopyResponseBody()
    {
        if (_selectedMessage == null) return;
        try { System.Windows.Clipboard.SetText(_selectedMessage.ResponseBody); StatusText = "Response Body 已复制到剪贴板"; }
        catch { }
    }

    #endregion

    #region Callbacks

    private void OnMessageCapturedCallback(CapturedHttpMessage msg)
    {
        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            lock (_allMessagesLock) { _allMessages.Insert(0, msg); }
            CapturedCount = _allMessages.Count;

            // Apply current display filter
            if (MatchesFilter(msg))
            {
                CapturedMessages.Insert(0, msg);
                while (CapturedMessages.Count > 2000)
                    CapturedMessages.RemoveAt(CapturedMessages.Count - 1);
                DisplayedCount = CapturedMessages.Count;
            }
        });
    }

    private void OnLogCallback(string msg)
    {
        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            StatusText = msg;
        });
    }

    #endregion

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
