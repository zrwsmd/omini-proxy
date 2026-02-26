using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using WowProxy.Domain;
using YamlDotNet.RepresentationModel;

namespace WowProxy.App;

internal static class NodeImport
{
    internal static async Task<(List<ProxyNode> Nodes, List<string> Errors)> LoadFromSubscriptionAsync(
        string url, CancellationToken cancellationToken, string? groupName = null)
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        var text = await http.GetStringAsync(url, cancellationToken);
        var (nodes, errors, _) = ParseText(text);

        // Tag nodes with their subscription group
        if (!string.IsNullOrWhiteSpace(groupName))
        {
            for (var i = 0; i < nodes.Count; i++)
            {
                nodes[i] = nodes[i] with { SubscriptionGroup = groupName };
            }
        }

        return (nodes, errors);
    }

    internal static (List<ProxyNode> Nodes, List<string> Errors, bool IsClash) ParseText(string text)
    {
        // 1. Try Clash YAML first (proxies: section)
        if (LooksLikeClashYaml(text))
        {
            var (clashNodes, clashErrors) = ParseClashYaml(text);
            if (clashNodes.Count > 0)
            {
                return (DeduplicateAndSort(clashNodes), clashErrors, true);
            }
        }

        // 2. Split into lines; if none look like proxy URIs, try base64-decode first
        var lines = SplitLines(text);
        var hasUriLines = lines.Any(l => l.TrimStart().StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)
                                      || l.TrimStart().StartsWith("vless://", StringComparison.OrdinalIgnoreCase)
                                      || l.TrimStart().StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)
                                      || l.TrimStart().StartsWith("ss://", StringComparison.OrdinalIgnoreCase));

        if (!hasUriLines)
        {
            var decoded = TryDecodeBase64ToText(text);
            if (decoded is not null)
            {
                // The decoded content might also be Clash YAML
                if (LooksLikeClashYaml(decoded))
                {
                    var (clashNodes, clashErrors) = ParseClashYaml(decoded);
                    if (clashNodes.Count > 0 || clashErrors.Count > 0)
                    {
                        return (DeduplicateAndSort(clashNodes), clashErrors, true);
                    }
                }

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

        return (DeduplicateAndSort(nodes), errors, false);
    }

    private static bool LooksLikeClashYaml(string text)
    {
        // A Clash subscription typically has "proxies:" near the top
        var slice = text.Length > 4096 ? text[..4096] : text;
        return slice.Contains("proxies:", StringComparison.Ordinal)
            || slice.Contains("Proxies:", StringComparison.Ordinal);
    }

    private static List<ProxyNode> DeduplicateAndSort(List<ProxyNode> nodes)
    {
        return nodes
            .GroupBy(n => n.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Clash YAML Parser ────────────────────────────────────────────────────

    private static (List<ProxyNode> Nodes, List<string> Errors) ParseClashYaml(string yaml)
    {
        var nodes = new List<ProxyNode>();
        var errors = new List<string>();

        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));

            if (stream.Documents.Count == 0)
            {
                return (nodes, errors);
            }

            var root = stream.Documents[0].RootNode as YamlMappingNode;
            if (root is null)
            {
                return (nodes, errors);
            }

            // Support both "proxies:" (Clash Meta / Clash.Premium) and legacy "Proxy:"
            YamlNode? proxiesNode = null;
            foreach (var entry in root.Children)
            {
                var key = (entry.Key as YamlScalarNode)?.Value ?? string.Empty;
                if (string.Equals(key, "proxies", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(key, "Proxy", StringComparison.OrdinalIgnoreCase))
                {
                    proxiesNode = entry.Value;
                    break;
                }
            }

            if (proxiesNode is not YamlSequenceNode proxiesList)
            {
                errors.Add("Clash YAML：未找到 proxies 列表");
                return (nodes, errors);
            }

            foreach (var item in proxiesList.Children)
            {
                if (item is not YamlMappingNode proxyMap)
                {
                    continue;
                }

                if (TryParseClashProxy(proxyMap, out var node, out var err))
                {
                    nodes.Add(node!);
                }
                else if (!string.IsNullOrWhiteSpace(err))
                {
                    errors.Add(err!);
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Clash YAML 解析失败：{ex.Message}");
        }

        return (nodes, errors);
    }

    private static bool TryParseClashProxy(YamlMappingNode map, out ProxyNode? node, out string? error)
    {
        node = null;
        error = null;

        var type = GetYamlString(map, "type")?.ToLowerInvariant() ?? string.Empty;
        var name = GetYamlString(map, "name") ?? string.Empty;
        var server = GetYamlString(map, "server") ?? string.Empty;
        var portText = GetYamlString(map, "port") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(server) || !int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            error = $"Clash 节点 \"{name}\"：server/port 无效";
            return false;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"{server}:{port}";
        }

        switch (type)
        {
            case "ss":
            case "shadowsocks":
                return TryParseClashShadowsocks(map, name, server, port, out node, out error);
            case "vmess":
                return TryParseClashVmess(map, name, server, port, out node, out error);
            case "trojan":
                return TryParseClashTrojan(map, name, server, port, out node, out error);
            case "vless":
                return TryParseClashVless(map, name, server, port, out node, out error);
            default:
                // Silently skip unsupported types (hysteria, tuic, etc.)
                return false;
        }
    }

    private static bool TryParseClashShadowsocks(YamlMappingNode map, string name, string server, int port,
        out ProxyNode? node, out string? error)
    {
        var password = GetYamlString(map, "password") ?? string.Empty;
        var cipher = GetYamlString(map, "cipher") ?? GetYamlString(map, "encrypt-method") ?? "aes-256-gcm";

        if (string.IsNullOrWhiteSpace(password))
        {
            node = null;
            error = $"Clash SS 节点 \"{name}\"：缺少 password";
            return false;
        }

        var raw = $"ss://clash-yaml#{name}@{server}:{port}?cipher={cipher}&password={Uri.EscapeDataString(password)}";

        node = new ProxyNode(
            Id: ProxyNode.IdFromRaw($"clash:ss:{server}:{port}:{cipher}:{password}:{name}"),
            Type: ProxyNodeType.Shadowsocks,
            Name: name,
            Server: server,
            Port: port,
            Password: password,
            Method: cipher,
            Raw: raw
        );

        error = null;
        return true;
    }

    private static bool TryParseClashVmess(YamlMappingNode map, string name, string server, int port,
        out ProxyNode? node, out string? error)
    {
        var uuid = GetYamlString(map, "uuid") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(uuid))
        {
            node = null;
            error = $"Clash VMess 节点 \"{name}\"：缺少 uuid";
            return false;
        }

        var alterIdText = GetYamlString(map, "alterId") ?? GetYamlString(map, "alter_id") ?? "0";
        int.TryParse(alterIdText, out var alterId);

        var cipher = GetYamlString(map, "cipher") ?? "auto";

        var tls = string.Equals(GetYamlString(map, "tls"), "true", StringComparison.OrdinalIgnoreCase)
                  || GetYamlBool(map, "tls");
        var sni = GetYamlString(map, "servername") ?? GetYamlString(map, "sni");
        var skipCertVerify = GetYamlBool(map, "skip-cert-verify");
        var fp = GetYamlString(map, "client-fingerprint");

        // network / transport
        var network = GetYamlString(map, "network") ?? string.Empty;
        string? transportType = network.ToLowerInvariant() switch
        {
            "ws" => "ws",
            "grpc" => "grpc",
            "h2" => "http",
            "http" => "http",
            "tcp" => null,
            _ => null,
        };

        string? wsHost = null;
        string? wsPath = null;
        if (string.Equals(network, "ws", StringComparison.OrdinalIgnoreCase))
        {
            var wsOpts = GetYamlMapping(map, "ws-opts") ?? GetYamlMapping(map, "ws-path");
            if (wsOpts is not null)
            {
                wsPath = GetYamlString(wsOpts, "path");
                var headersNode = GetYamlMapping(wsOpts, "headers");
                if (headersNode is not null)
                {
                    wsHost = GetYamlString(headersNode, "Host") ?? GetYamlString(headersNode, "host");
                }
            }
        }

        var alpnList = GetYamlStringList(map, "alpn");
        var alpn = alpnList.Count > 0 ? string.Join(",", alpnList) : null;

        var raw = $"vmess://clash-yaml:{name}@{server}:{port}";

        node = new ProxyNode(
            Id: ProxyNode.IdFromRaw($"clash:vmess:{server}:{port}:{uuid}:{alterId}:{name}"),
            Type: ProxyNodeType.Vmess,
            Name: name,
            Server: server,
            Port: port,
            Uuid: uuid,
            AlterId: alterId,
            Security: cipher,
            TlsEnabled: tls,
            TlsServerName: string.IsNullOrWhiteSpace(sni) ? null : sni,
            TlsInsecure: skipCertVerify,
            TlsAlpn: alpn,
            UtlsFingerprint: string.IsNullOrWhiteSpace(fp) ? null : fp,
            TransportType: transportType,
            TransportHost: string.IsNullOrWhiteSpace(wsHost) ? null : wsHost,
            TransportPath: string.IsNullOrWhiteSpace(wsPath) ? null : NormalizePath(wsPath),
            Raw: raw
        );

        error = null;
        return true;
    }

    private static bool TryParseClashTrojan(YamlMappingNode map, string name, string server, int port,
        out ProxyNode? node, out string? error)
    {
        var password = GetYamlString(map, "password") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(password))
        {
            node = null;
            error = $"Clash Trojan 节点 \"{name}\"：缺少 password";
            return false;
        }

        var sni = GetYamlString(map, "sni") ?? GetYamlString(map, "servername");
        var skipCertVerify = GetYamlBool(map, "skip-cert-verify");
        var fp = GetYamlString(map, "client-fingerprint");
        var alpnList = GetYamlStringList(map, "alpn");
        var alpn = alpnList.Count > 0 ? string.Join(",", alpnList) : null;

        var network = GetYamlString(map, "network") ?? string.Empty;
        string? transportType = network.ToLowerInvariant() switch
        {
            "ws" => "ws",
            "grpc" => "grpc",
            _ => null,
        };

        string? wsHost = null;
        string? wsPath = null;
        if (string.Equals(network, "ws", StringComparison.OrdinalIgnoreCase))
        {
            var wsOpts = GetYamlMapping(map, "ws-opts");
            if (wsOpts is not null)
            {
                wsPath = GetYamlString(wsOpts, "path");
                var headersNode = GetYamlMapping(wsOpts, "headers");
                if (headersNode is not null)
                {
                    wsHost = GetYamlString(headersNode, "Host") ?? GetYamlString(headersNode, "host");
                }
            }
        }

        var raw = $"trojan://clash-yaml:{name}@{server}:{port}";

        node = new ProxyNode(
            Id: ProxyNode.IdFromRaw($"clash:trojan:{server}:{port}:{password}:{name}"),
            Type: ProxyNodeType.Trojan,
            Name: name,
            Server: server,
            Port: port,
            Password: password,
            TlsEnabled: true,
            TlsServerName: string.IsNullOrWhiteSpace(sni) ? null : sni,
            TlsInsecure: skipCertVerify,
            TlsAlpn: alpn,
            UtlsFingerprint: string.IsNullOrWhiteSpace(fp) ? null : fp,
            TransportType: transportType,
            TransportHost: string.IsNullOrWhiteSpace(wsHost) ? null : wsHost,
            TransportPath: string.IsNullOrWhiteSpace(wsPath) ? null : NormalizePath(wsPath),
            Raw: raw
        );

        error = null;
        return true;
    }

    private static bool TryParseClashVless(YamlMappingNode map, string name, string server, int port,
        out ProxyNode? node, out string? error)
    {
        var uuid = GetYamlString(map, "uuid") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(uuid))
        {
            node = null;
            error = $"Clash VLESS 节点 \"{name}\"：缺少 uuid";
            return false;
        }

        var tls = string.Equals(GetYamlString(map, "tls"), "true", StringComparison.OrdinalIgnoreCase)
                  || GetYamlBool(map, "tls");
        var security = GetYamlString(map, "security");
        var isReality = string.Equals(security, "reality", StringComparison.OrdinalIgnoreCase);
        if (isReality)
        {
            tls = true;
        }

        var sni = GetYamlString(map, "servername") ?? GetYamlString(map, "sni");
        var skipCertVerify = GetYamlBool(map, "skip-cert-verify");
        var fp = GetYamlString(map, "client-fingerprint");
        var flow = GetYamlString(map, "flow");
        var alpnList = GetYamlStringList(map, "alpn");
        var alpn = alpnList.Count > 0 ? string.Join(",", alpnList) : null;

        // Reality options
        string? realityPbk = null;
        string? realitySid = null;
        var realityOpts = GetYamlMapping(map, "reality-opts");
        if (realityOpts is not null)
        {
            realityPbk = GetYamlString(realityOpts, "public-key");
            realitySid = GetYamlString(realityOpts, "short-id");
        }

        var network = GetYamlString(map, "network") ?? string.Empty;
        string? transportType = network.ToLowerInvariant() switch
        {
            "ws" => "ws",
            "grpc" => "grpc",
            "h2" => "http",
            "http" => "http",
            "tcp" => null,
            _ => null,
        };

        string? wsHost = null;
        string? wsPath = null;
        if (string.Equals(network, "ws", StringComparison.OrdinalIgnoreCase))
        {
            var wsOpts = GetYamlMapping(map, "ws-opts");
            if (wsOpts is not null)
            {
                wsPath = GetYamlString(wsOpts, "path");
                var headersNode = GetYamlMapping(wsOpts, "headers");
                if (headersNode is not null)
                {
                    wsHost = GetYamlString(headersNode, "Host") ?? GetYamlString(headersNode, "host");
                }
            }
        }

        var raw = $"vless://clash-yaml:{name}@{server}:{port}";

        node = new ProxyNode(
            Id: ProxyNode.IdFromRaw($"clash:vless:{server}:{port}:{uuid}:{name}"),
            Type: ProxyNodeType.Vless,
            Name: name,
            Server: server,
            Port: port,
            Uuid: uuid,
            Security: isReality ? "reality" : (tls ? "tls" : null),
            TlsEnabled: tls,
            TlsServerName: string.IsNullOrWhiteSpace(sni) ? null : sni,
            TlsInsecure: skipCertVerify,
            TlsAlpn: alpn,
            UtlsFingerprint: string.IsNullOrWhiteSpace(fp) ? null : fp,
            RealityPublicKey: string.IsNullOrWhiteSpace(realityPbk) ? null : realityPbk,
            RealityShortId: string.IsNullOrWhiteSpace(realitySid) ? null : realitySid,
            Flow: string.IsNullOrWhiteSpace(flow) ? null : flow,
            TransportType: transportType,
            TransportHost: string.IsNullOrWhiteSpace(wsHost) ? null : wsHost,
            TransportPath: string.IsNullOrWhiteSpace(wsPath) ? null : NormalizePath(wsPath),
            Raw: raw
        );

        error = null;
        return true;
    }

    // ── YAML helpers ─────────────────────────────────────────────────────────

    private static string? GetYamlString(YamlMappingNode map, string key)
    {
        foreach (var entry in map.Children)
        {
            if (entry.Key is YamlScalarNode k && string.Equals(k.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                return (entry.Value as YamlScalarNode)?.Value;
            }
        }

        return null;
    }

    private static bool GetYamlBool(YamlMappingNode map, string key)
    {
        var val = GetYamlString(map, key);
        return string.Equals(val, "true", StringComparison.OrdinalIgnoreCase)
            || val == "1";
    }

    private static YamlMappingNode? GetYamlMapping(YamlMappingNode map, string key)
    {
        foreach (var entry in map.Children)
        {
            if (entry.Key is YamlScalarNode k && string.Equals(k.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value as YamlMappingNode;
            }
        }

        return null;
    }

    private static List<string> GetYamlStringList(YamlMappingNode map, string key)
    {
        var result = new List<string>();
        foreach (var entry in map.Children)
        {
            if (entry.Key is YamlScalarNode k && string.Equals(k.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                if (entry.Value is YamlSequenceNode seq)
                {
                    foreach (var item in seq.Children)
                    {
                        if (item is YamlScalarNode s && !string.IsNullOrWhiteSpace(s.Value))
                        {
                            result.Add(s.Value!);
                        }
                    }
                }
                else if (entry.Value is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value))
                {
                    // Single value written as scalar
                    result.Add(scalar.Value!);
                }

                break;
            }
        }

        return result;
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
        // Strip all whitespace (newlines, spaces) from base64 payload before decoding
        var payload = new string(raw["vmess://".Length..].Where(c => !char.IsWhiteSpace(c)).ToArray());
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
