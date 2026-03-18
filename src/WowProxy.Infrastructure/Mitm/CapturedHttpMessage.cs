namespace WowProxy.Infrastructure.Mitm;

/// <summary>
/// Represents a captured HTTP request/response pair from MITM interception.
/// </summary>
public sealed class CapturedHttpMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string Method { get; set; } = "";
    public string Url { get; set; } = "";
    public string Host { get; set; } = "";
    public Dictionary<string, string> RequestHeaders { get; set; } = new();
    public string RequestBody { get; set; } = "";
    public int StatusCode { get; set; }
    public Dictionary<string, string> ResponseHeaders { get; set; } = new();
    public string ResponseBody { get; set; } = "";
    public long DurationMs { get; set; }
    public string? Error { get; set; }
    public bool IsCompleted { get; set; }

    public string DisplaySummary =>
        $"{Method} {Url} → {(IsCompleted ? StatusCode.ToString() : "...")}";
}
