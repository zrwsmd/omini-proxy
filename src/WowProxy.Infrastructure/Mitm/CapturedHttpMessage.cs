namespace WowProxy.Infrastructure.Mitm;

/// <summary>
/// Represents a captured HTTP request/response pair from MITM interception.
/// All fields are available for post-capture filtering.
/// </summary>
public sealed class CapturedHttpMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;

    // Connection info
    public string Protocol { get; set; } = "HTTPS";   // HTTP or HTTPS
    public string Host { get; set; } = "";             // SNI / Host header
    public string RemoteAddress { get; set; } = "";    // destination IP:port from CONNECT
    public int RemotePort { get; set; }

    // Request
    public string Method { get; set; } = "";
    public string Url { get; set; } = "";
    public string Path { get; set; } = "";
    public Dictionary<string, string> RequestHeaders { get; set; } = new();
    public string RequestBody { get; set; } = "";
    public string RequestContentType { get; set; } = "";

    // Response
    public int StatusCode { get; set; }
    public Dictionary<string, string> ResponseHeaders { get; set; } = new();
    public string ResponseBody { get; set; } = "";
    public string ResponseContentType { get; set; } = "";

    // Meta
    public long DurationMs { get; set; }
    public long RequestSize { get; set; }
    public long ResponseSize { get; set; }
    public string? Error { get; set; }
    public bool IsCompleted { get; set; }

    /// <summary>Get a field value by name for display-filter matching.</summary>
    public string GetField(string fieldName)
    {
        return fieldName.ToLowerInvariant() switch
        {
            "host" or "域名" or "sni" => Host,
            "url" => Url,
            "path" or "路径" => Path,
            "method" or "方法" => Method,
            "status" or "状态码" => StatusCode.ToString(),
            "protocol" or "协议" => Protocol,
            "ip" or "目的ip" or "remoteaddress" => RemoteAddress,
            "port" or "端口" => RemotePort.ToString(),
            "req.content-type" or "请求类型" => RequestContentType,
            "resp.content-type" or "响应类型" => ResponseContentType,
            "req.body" or "请求体" => RequestBody,
            "resp.body" or "响应体" => ResponseBody,
            "req.header" or "请求头" => string.Join("\n", RequestHeaders.Select(h => $"{h.Key}: {h.Value}")),
            "resp.header" or "响应头" => string.Join("\n", ResponseHeaders.Select(h => $"{h.Key}: {h.Value}")),
            "error" or "错误" => Error ?? "",
            "duration" or "耗时" => DurationMs.ToString(),
            "size" or "大小" => ResponseSize.ToString(),
            "all" or "全部" => $"{Method} {Url} {Host} {RequestBody} {ResponseBody}",
            _ => "",
        };
    }

    /// <summary>List of all supported filter field names.</summary>
    public static readonly string[] FilterFieldNames =
    {
        "host", "url", "path", "method", "status", "protocol",
        "ip", "port", "req.content-type", "resp.content-type",
        "req.body", "resp.body", "req.header", "resp.header",
        "error", "duration", "all"
    };
}
