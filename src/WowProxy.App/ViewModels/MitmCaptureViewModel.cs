using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using WowProxy.Infrastructure.Mitm;

namespace WowProxy.App.ViewModels;

/// <summary>
/// ViewModel for the MITM packet capture tab.
/// Captures ALL traffic. Single mitmproxy-style search bar with prefix commands:
///   ~d domain, ~u url, ~m method, ~c status, ~b body, ~bq req body, ~bs resp body,
///   ~h header, ~hq req header, ~hs resp header, ~dst ip, ~e error, ~t content-type
/// No prefix = fuzzy match all fields. Append " regex" or use ~d regex pattern.
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
    private string _statusText = "就绪 — 输入搜索条件过滤，如 ~d openai 或 ~b system，留空显示全部";
    private bool _caInstalled;

    // All captured messages (unfiltered)
    private readonly List<CapturedHttpMessage> _allMessages = new();
    private readonly object _allMessagesLock = new();

    // Single search text (mitmproxy-style)
    private string _searchText = "";
    private int _capturedCount;
    private int _displayedCount;

    /// <summary>Supported search prefixes (mitmproxy-style).</summary>
    private static readonly Dictionary<string, string> PrefixToField = new(StringComparer.OrdinalIgnoreCase)
    {
        ["~d"]       = "host",
        ["~u"]       = "url",
        ["~path"]    = "path",
        ["~m"]       = "method",
        ["~c"]       = "status",
        ["~b"]       = "all_body",
        ["~bq"]      = "req.body",
        ["~bs"]      = "resp.body",
        ["~h"]       = "all_header",
        ["~hq"]      = "req.header",
        ["~hs"]      = "resp.header",
        ["~dst"]     = "ip",
        ["~e"]       = "error",
        ["~t"]       = "content_type",
        ["~s"]       = "status",
        ["~all"]     = "all",
    };

    public MitmCaptureViewModel()
    {
        _ca = new MitmCertificateAuthority();
        CapturedMessages = new ObservableCollection<CapturedHttpMessage>();

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

    /// <summary>Search hint text shown as watermark.</summary>
    public static string SearchHint =>
        "搜索: 直接输入模糊匹配 | ~d 域名 | ~u URL | ~m 方法 | ~c 状态码 | ~b Body | ~bq 请求体 | ~bs 响应体 | ~h Header | ~dst IP | ~e 错误 | 支持正则";

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

    // Single search bar
    public string SearchText
    {
        get => _searchText;
        set { if (_searchText != value) { _searchText = value; OnPropertyChanged(); } }
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
                OnPropertyChanged(nameof(SelectedQueryParams));
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
                   $"请求: {FormatSize(msg.RequestSize)}  响应: {FormatSize(msg.ResponseSize)}" +
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

    /// <summary>Parse URL query string into a readable key=value view.</summary>
    public string SelectedQueryParams
    {
        get
        {
            if (_selectedMessage == null) return "";
            var url = _selectedMessage.Url;
            var qIdx = url.IndexOf('?');
            if (qIdx < 0 || qIdx >= url.Length - 1) return "(无 Query 参数)";

            var queryString = url[(qIdx + 1)..];
            // Remove fragment
            var fragIdx = queryString.IndexOf('#');
            if (fragIdx >= 0) queryString = queryString[..fragIdx];

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Path: {_selectedMessage.Path?.Split('?')[0]}");
            sb.AppendLine();

            var pairs = queryString.Split('&', StringSplitOptions.RemoveEmptyEntries);
            var maxKeyLen = 0;
            var parsed = new List<(string key, string value)>();
            foreach (var pair in pairs)
            {
                var eqIdx = pair.IndexOf('=');
                string key, val;
                if (eqIdx >= 0)
                {
                    key = Uri.UnescapeDataString(pair[..eqIdx]);
                    val = Uri.UnescapeDataString(pair[(eqIdx + 1)..]);
                }
                else
                {
                    key = Uri.UnescapeDataString(pair);
                    val = "";
                }
                parsed.Add((key, val));
                if (key.Length > maxKeyLen) maxKeyLen = key.Length;
            }

            foreach (var (key, val) in parsed)
            {
                sb.AppendLine($"{key.PadRight(maxKeyLen)}  =  {val}");
            }

            sb.AppendLine();
            sb.AppendLine($"共 {parsed.Count} 个参数");
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
            _server.OnMessageCaptured += OnMessageCapturedCallback;
            _server.OnLog += OnLogCallback;
            _server.Start(port);

            IsRunning = true;
            StatusText = $"抓包运行中 — 127.0.0.1:{port} — 输入搜索条件过滤";
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

    #region Filter — mitmproxy-style single search bar

    private void ApplyFilter()
    {
        RebuildDisplayList();
        StatusText = string.IsNullOrWhiteSpace(SearchText)
            ? $"显示全部 — {CountText}"
            : $"过滤: {SearchText} — {CountText}";
    }

    private void ClearFilter()
    {
        SearchText = "";
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
            if (MatchesSearch(msg))
                CapturedMessages.Add(msg);
        }

        DisplayedCount = CapturedMessages.Count;
    }

    /// <summary>
    /// Parse mitmproxy-style search text and match against a message.
    /// Format: [~prefix] value
    /// No prefix → fuzzy match all fields (contains, case-insensitive).
    /// Prefix → match specific field. Value is treated as regex if it looks like one,
    /// otherwise as case-insensitive contains (fuzzy).
    /// </summary>
    private bool MatchesSearch(CapturedHttpMessage msg)
    {
        var text = SearchText?.Trim();
        if (string.IsNullOrEmpty(text)) return true;

        // Parse prefix
        string field = "all";
        string pattern = text;

        if (text.StartsWith('~'))
        {
            var spaceIdx = text.IndexOf(' ');
            if (spaceIdx > 0)
            {
                var prefix = text[..spaceIdx];
                if (PrefixToField.TryGetValue(prefix, out var mappedField))
                {
                    field = mappedField;
                    pattern = text[(spaceIdx + 1)..].Trim();
                }
            }
            else
            {
                // Just prefix with no value, e.g. "~e" → show only messages with errors
                if (PrefixToField.TryGetValue(text, out var mappedField))
                {
                    field = mappedField;
                    pattern = "";
                }
            }
        }

        // Get target field value(s) to search
        string fieldValue;
        if (field == "all_body")
            fieldValue = msg.RequestBody + "\n" + msg.ResponseBody;
        else if (field == "all_header")
            fieldValue = msg.GetField("req.header") + "\n" + msg.GetField("resp.header");
        else if (field == "content_type")
            fieldValue = msg.RequestContentType + "\n" + msg.ResponseContentType;
        else
            fieldValue = msg.GetField(field);

        // Special case: prefix with no pattern (e.g. ~e) → just check non-empty
        if (string.IsNullOrEmpty(pattern))
            return !string.IsNullOrEmpty(fieldValue);

        // Try regex first, fall back to fuzzy contains
        try
        {
            if (IsLikelyRegex(pattern))
                return Regex.IsMatch(fieldValue, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }
        catch { /* not valid regex, fall through to fuzzy */ }

        // Fuzzy contains match
        return fieldValue.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Detect whether a pattern is likely a regex (has special regex chars).</summary>
    private static bool IsLikelyRegex(string pattern)
    {
        // If pattern contains regex-special characters, treat as regex
        return pattern.IndexOfAny(new[] { '|', '(', ')', '[', ']', '{', '}', '\\', '^', '$', '+', '?', '.' }) >= 0
               && pattern.Length > 1;
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

            if (MatchesSearch(msg))
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

    /// <summary>Format byte size to human-readable string like mitmproxy (e.g. 1.2kb).</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0";
        if (bytes < 1024) return $"{bytes}b";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#}kb";
        return $"{bytes / (1024.0 * 1024.0):0.#}mb";
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
                var options = new System.Text.Json.JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                return System.Text.Json.JsonSerializer.Serialize(doc, options);
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
