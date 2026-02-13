using System.Security.Cryptography;
using System.Text;

namespace WowProxy.Domain;

public enum ProxyNodeType
{
    Vless = 1,
    Trojan = 2,
    Vmess = 3,
    Shadowsocks = 4,
}

public sealed record ProxyNode(
    string Id,
    ProxyNodeType Type,
    string Name,
    string Server,
    int Port,
    string? Uuid = null,
    string? Password = null,
    string? Method = null,
    int? AlterId = null,
    string? Security = null,
    bool TlsEnabled = false,
    string? TlsServerName = null,
    string? Flow = null,
    string? TransportType = null,
    string? TransportHost = null,
    string? TransportPath = null,
    string Raw = ""
)
{
    public static string IdFromRaw(string raw)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw.Trim()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

