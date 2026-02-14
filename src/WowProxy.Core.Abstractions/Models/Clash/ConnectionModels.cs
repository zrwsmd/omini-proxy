using System.Text.Json.Serialization;

namespace WowProxy.Core.Abstractions.Models.Clash;

public class ConnectionsResponse
{
    [JsonPropertyName("downloadTotal")]
    public long DownloadTotal { get; set; }

    [JsonPropertyName("uploadTotal")]
    public long UploadTotal { get; set; }

    [JsonPropertyName("connections")]
    public List<Connection> Connections { get; set; } = new();
}

public class Connection
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("metadata")]
    public ConnectionMetadata Metadata { get; set; } = new();

    [JsonPropertyName("upload")]
    public long Upload { get; set; }

    [JsonPropertyName("download")]
    public long Download { get; set; }

    [JsonPropertyName("start")]
    public DateTime Start { get; set; }

    [JsonPropertyName("chains")]
    public List<string> Chains { get; set; } = new();

    [JsonPropertyName("rule")]
    public string Rule { get; set; } = string.Empty;

    [JsonPropertyName("rulePayload")]
    public string RulePayload { get; set; } = string.Empty;
}

public class ConnectionMetadata
{
    [JsonPropertyName("network")]
    public string Network { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("sourceIP")]
    public string SourceIP { get; set; } = string.Empty;

    [JsonPropertyName("destinationIP")]
    public string DestinationIP { get; set; } = string.Empty;

    [JsonPropertyName("sourcePort")]
    public string SourcePort { get; set; } = string.Empty;

    [JsonPropertyName("destinationPort")]
    public string DestinationPort { get; set; } = string.Empty;

    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("processPath")]
    public string ProcessPath { get; set; } = string.Empty;
    
    [JsonPropertyName("process")]
    public string Process { get; set; } = string.Empty;
}
