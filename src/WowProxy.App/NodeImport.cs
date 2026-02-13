using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using WowProxy.Domain;

namespace WowProxy.App;

internal static class NodeImport
{
    internal static async Task<(List<ProxyNode> Nodes, List<string> Errors)> LoadFromSubscriptionAsync(string url, CancellationToken cancellationToken)
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        var text = await http.GetStringAsync(url, cancellationToken);
        return ParseText(text);
    }

    internal static (List<ProxyNode> Nodes, List<string> Errors) ParseText(string text)
    {
        var lines = SplitLines(text);
        if (lines.Count == 0)
        {
            var decoded = TryDecodeBase64ToText(text);
            if (decoded is not null)
            {
                lines = SplitLines(decoded);
            }
        }

        var nodes = new List<ProxyNode>();
        var errors = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (TryParseNode(trimmed, out var node, out var error))
            {
                nodes.Add(node);
            }
            else
            {
                errors.Add(error);
            }
        }

        nodes = nodes
            .GroupBy(n => n.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (nodes, errors);
    }

    private static List<string> SplitLines(string text)
    {
        var list = new List<string>();
        using var reader = new StringReader(text);
        while (true)
        {
            var line = reader.ReadLine();
            if (line is null)
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                list.Add(line);
            }
        }

        if (list.Count > 0)
        {
            return list;
        }

        return text.Contains("://", StringComparison.OrdinalIgnoreCase)
            ? new List<string> { text }
            : new List<string>();
    }

    private static string? TryDecodeBase64ToText(string text)
    {
        var compact = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (compact.Length < 8)
        {
            return null;
        }

        if (compact.Contains("://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (TryFromBase64(compact, out var bytes) || TryFromBase64(ToBase64Standard(compact), out bytes))
        {
            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static bool TryFromBase64(string input, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(PadBase64(input));
            return true;
        }
        catch
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }

    private static string PadBase64(string s)
    {
        var mod = s.Length % 4;
        return mod == 0 ? s : s + new string('=', 4 - mod);
    }

    private static string ToBase64Standard(string s) => s.Replace('-', '+').Replace('_', '/');

    internal static bool TryParseNode(string raw, out ProxyNode node, out string error)
    {
        try
        {
            if (raw.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
            {
                return TryParseVless(raw, out node, out error);
            }

            if (raw.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase))
            {
                return TryParseTrojan(raw, out node, out error);
            }

            if (raw.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
            {
                return TryParseVmess(raw, out node, out error);
            }

            if (raw.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
            {
                return TryParseShadowsocks(raw, out node, out error);
            }

            node = default!;
            error = $"不支持的链接：{raw[..Math.Min(raw.Length, 32)]}";
            return false;
        }
        catch (Exception ex)
        {
            node = default!;
            error = $"解析失败：{ex.Message}";
            return false;
        }
    }

    private static bool TryParseVless(string raw, out ProxyNode node, out string error)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            node = default!;
            error = "vless 链接无效";
            return false;
        }

        var uuid = uri.UserInfo;
        if (string.IsNullOrWhiteSpace(uuid))
        {
            node = default!;
            error = "vless 缺少 uuid";
            return false;
        }

        var query = ParseQuery(uri.Query);
        var security = query.TryGetValue("security", out var sec) ? sec : null;
        var tlsEnabled = string.Equals(security, "tls", StringComparison.OrdinalIgnoreCase)
            || string.Equals(security, "reality", StringComparison.OrdinalIgnoreCase);
        var sni = query.TryGetValue("sni", out var sniValue) ? sniValue : null;
        var flow = query.TryGetValue("flow", out var flowValue) ? flowValue : null;
        var fp = query.TryGetValue("fp", out var fpValue) ? fpValue : null;
        var pbk = query.TryGetValue("pbk", out var pbkValue) ? pbkValue : null;
        var sid = query.TryGetValue("sid", out var sidValue) ? sidValue : null;
        var alpn = query.TryGetValue("alpn", out var alpnValue) ? alpnValue : null;
        var insecure =
            (query.TryGetValue("allowInsecure", out var allowInsecureValue) && allowInsecureValue is "1" or "true" or "True")
            || (query.TryGetValue("insecure", out var insecureValue) && insecureValue is "1" or "true" or "True");

        var transport = query.TryGetValue("type", out var typeValue) ? typeValue : null;
        transport ??= query.TryGetValue("transport", out var transportValue) ? transportValue : null;
        transport = string.IsNullOrWhiteSpace(transport) ? null : transport;

        var host = query.TryGetValue("host", out var hostValue) ? hostValue : null;
        var path = query.TryGetValue("path", out var pathValue) ? pathValue : null;
        path = NormalizePath(path);

        var name = !string.IsNullOrWhiteSpace(uri.Fragment) ? uri.Fragment.TrimStart('#') : $"{uri.Host}:{uri.Port}";
        name = Uri.UnescapeDataString(name);

        var port = uri.Port;
        if (port <= 0)
        {
            port = tlsEnabled ? 443 : 80;
        }

        node = new ProxyNode(
            Id: ProxyNode.IdFromRaw(raw),
            Type: ProxyNodeType.Vless,
            Name: string.IsNullOrWhiteSpace(name) ? $"{uri.Host}:{uri.Port}" : name,
            Server: uri.Host,
            Port: port,
            Uuid: uuid,
            Security: security,
            TlsEnabled: tlsEnabled,
            TlsServerName: string.IsNullOrWhiteSpace(sni) ? null : sni,
            TlsInsecure: insecure,
            TlsAlpn: string.IsNullOrWhiteSpace(alpn) ? null : alpn,
            UtlsFingerprint: string.IsNullOrWhiteSpace(fp) ? null : fp,
            RealityPublicKey: string.IsNullOrWhiteSpace(pbk) ? null : pbk,
            RealityShortId: string.IsNullOrWhiteSpace(sid) ? null : sid,
            Flow: flow,
            TransportType: transport,
            TransportHost: string.IsNullOrWhiteSpace(host) ? null : host,
            TransportPath: string.IsNullOrWhiteSpace(path) ? null : path,
            Raw: raw
        );

        error = string.Empty;
        return true;
    }

    private static bool TryParseTrojan(string raw, out ProxyNode node, out string error)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            node = default!;
            error = "trojan 链接无效";
            return false;
        }

        var password = uri.UserInfo;
        if (string.IsNullOrWhiteSpace(password))
        {
            node = default!;
            error = "trojan 缺少 password";
            return false;
        }

        var query = ParseQuery(uri.Query);
        var security = query.TryGetValue("security", out var sec) ? sec : null;
        var fp = query.TryGetValue("fp", out var fpValue) ? fpValue : null;
        var alpn = query.TryGetValue("alpn", out var alpnValue) ? alpnValue : null;
        var insecure =
            (query.TryGetValue("allowInsecure", out var allowInsecureValue) && allowInsecureValue is "1" or "true" or "True")
            || (query.TryGetValue("insecure", out var insecureValue) && insecureValue is "1" or "true" or "True");
        var tlsEnabled = !string.Equals(security, "none", StringComparison.OrdinalIgnoreCase);
        var sni = query.TryGetValue("sni", out var sniValue) ? sniValue : null;

        var transport = query.TryGetValue("type", out var typeValue) ? typeValue : null;
        transport = string.IsNullOrWhiteSpace(transport) ? null : transport;

        var host = query.TryGetValue("host", out var hostValue) ? hostValue : null;
        var path = query.TryGetValue("path", out var pathValue) ? pathValue : null;
        path = NormalizePath(path);

        var port = uri.Port;
        if (port <= 0)
        {
            port = 443;
        }

        var name = !string.IsNullOrWhiteSpace(uri.Fragment) ? uri.Fragment.TrimStart('#') : $"{uri.Host}:{port}";
        name = Uri.UnescapeDataString(name);

        node = new ProxyNode(
            Id: ProxyNode.IdFromRaw(raw),
            Type: ProxyNodeType.Trojan,
            Name: string.IsNullOrWhiteSpace(name) ? $"{uri.Host}:{uri.Port}" : name,
            Server: uri.Host,
            Port: port,
            Password: password,
            Security: security,
            TlsEnabled: tlsEnabled,
            TlsServerName: string.IsNullOrWhiteSpace(sni) ? null : sni,
            TlsInsecure: insecure,
            TlsAlpn: string.IsNullOrWhiteSpace(alpn) ? null : alpn,
            UtlsFingerprint: string.IsNullOrWhiteSpace(fp) ? null : fp,
            TransportType: transport,
            TransportHost: string.IsNullOrWhiteSpace(host) ? null : host,
            TransportPath: string.IsNullOrWhiteSpace(path) ? null : path,
            Raw: raw
        );

        error = string.Empty;
        return true;
    }

    private static bool TryParseVmess(string raw, out ProxyNode node, out string error)
    {
        var payload = raw["vmess://".Length..].Trim();
        if (!TryFromBase64(payload, out var bytes) && !TryFromBase64(ToBase64Standard(payload), out bytes))
        {
            node = default!;
            error = "vmess base64 无效";
            return false;
        }

        var json = Encoding.UTF8.GetString(bytes);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var server = GetString(root, "add") ?? string.Empty;
        var portText = GetString(root, "port") ?? string.Empty;
        var uuid = GetString(root, "id") ?? string.Empty;
        var alterIdText = GetString(root, "aid");
        var net = GetString(root, "net");
        var host = GetString(root, "host");
        var path = GetString(root, "path");
        var tls = GetString(root, "tls");
        var sni = GetString(root, "sni");
        var fp = GetString(root, "fp");
        var alpn = GetString(root, "alpn");
        var allowInsecure = GetString(root, "allowInsecure") ?? GetString(root, "allowinsecure");
        var insecure = allowInsecure is "1" or "true" or "True";
        var ps = GetString(root, "ps");

        if (string.IsNullOrWhiteSpace(server) || !int.TryParse(portText, out var port) || port is < 1 or > 65535 || string.IsNullOrWhiteSpace(uuid))
        {
            node = default!;
            error = "vmess 缺少必要字段";
            return false;
        }

        int? alterId = null;
        if (int.TryParse(alterIdText, out var aid))
        {
            alterId = aid;
        }

        var tlsEnabled = string.Equals(tls, "tls", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(sni) && tlsEnabled && !string.IsNullOrWhiteSpace(host))
        {
            sni = host;
        }
        var name = string.IsNullOrWhiteSpace(ps) ? $"{server}:{port}" : ps;
        path = NormalizePath(path);

        node = new ProxyNode(
            Id: ProxyNode.IdFromRaw(raw),
            Type: ProxyNodeType.Vmess,
            Name: name,
            Server: server,
            Port: port,
            Uuid: uuid,
            AlterId: alterId,
            Security: "auto",
            TlsEnabled: tlsEnabled,
            TlsServerName: string.IsNullOrWhiteSpace(sni) ? null : sni,
            TlsInsecure: insecure,
            TlsAlpn: string.IsNullOrWhiteSpace(alpn) ? null : alpn,
            UtlsFingerprint: string.IsNullOrWhiteSpace(fp) ? null : fp,
            TransportType: string.IsNullOrWhiteSpace(net) ? null : net,
            TransportHost: string.IsNullOrWhiteSpace(host) ? null : host,
            TransportPath: string.IsNullOrWhiteSpace(path) ? null : path,
            Raw: raw
        );

        error = string.Empty;
        return true;
    }

    private static bool TryParseShadowsocks(string raw, out ProxyNode node, out string error)
    {
        var noScheme = raw["ss://".Length..];
        var fragmentIndex = noScheme.IndexOf('#');
        var fragment = fragmentIndex >= 0 ? noScheme[fragmentIndex..] : string.Empty;
        var main = fragmentIndex >= 0 ? noScheme[..fragmentIndex] : noScheme;

        main = main.Trim();
        fragment = fragment.Trim();

        string? name = null;
        if (!string.IsNullOrWhiteSpace(fragment))
        {
            name = Uri.UnescapeDataString(fragment.TrimStart('#'));
        }

        var userInfoAndHost = main;
        if (!userInfoAndHost.Contains('@'))
        {
            if (!TryFromBase64(userInfoAndHost, out var bytes) && !TryFromBase64(ToBase64Standard(userInfoAndHost), out bytes))
            {
                node = default!;
                error = "ss base64 无效";
                return false;
            }

            userInfoAndHost = Encoding.UTF8.GetString(bytes);
        }

        var at = userInfoAndHost.LastIndexOf('@');
        if (at <= 0)
        {
            node = default!;
            error = "ss 格式无效";
            return false;
        }

        var userInfo = userInfoAndHost[..at];
        var hostPart = userInfoAndHost[(at + 1)..];

        var colon = userInfo.IndexOf(':');
        if (colon <= 0)
        {
            if (!TryFromBase64(userInfo, out var bytes) && !TryFromBase64(ToBase64Standard(userInfo), out bytes))
            {
                node = default!;
                error = "ss 缺少 method:password";
                return false;
            }

            userInfo = Encoding.UTF8.GetString(bytes);
            colon = userInfo.IndexOf(':');
            if (colon <= 0)
            {
                node = default!;
                error = "ss 缺少 method:password";
                return false;
            }
        }

        var method = userInfo[..colon];
        var password = userInfo[(colon + 1)..];

        if (!Uri.TryCreate("ss://" + hostPart, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.Port <= 0)
        {
            node = default!;
            error = "ss 缺少 host:port";
            return false;
        }

        node = new ProxyNode(
            Id: ProxyNode.IdFromRaw(raw),
            Type: ProxyNodeType.Shadowsocks,
            Name: string.IsNullOrWhiteSpace(name) ? $"{uri.Host}:{uri.Port}" : name,
            Server: uri.Host,
            Port: uri.Port,
            Password: password,
            Method: method,
            Raw: raw
        );

        error = string.Empty;
        return true;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return dict;
        }

        var q = query.TrimStart('?');
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0)
            {
                dict[Uri.UnescapeDataString(part)] = string.Empty;
                continue;
            }

            var key = Uri.UnescapeDataString(part[..idx]);
            var value = Uri.UnescapeDataString(part[(idx + 1)..]);
            dict[key] = value;
        }

        return dict;
    }

    private static string? GetString(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!obj.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => prop.GetRawText(),
        };
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var p = path.Trim();
        if (!p.StartsWith('/'))
        {
            p = "/" + p;
        }

        return p;
    }
}
