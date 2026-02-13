using System.Linq;
using System.Text;
using System.Text.Json;
using WowProxy.Domain;

namespace WowProxy.Core.SingBox;

public sealed class SingBoxConfigFactoryV2
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public string Build(AppSettings settings)
    {
        var selected = ResolveSelectedNode(settings);
        var hasProxy = selected is not null;
        var logLevel = NormalizeLogLevel(settings.LogLevel);

        var root = new Dictionary<string, object?>
        {
            ["log"] = new
            {
                level = logLevel,
                timestamp = true,
            },
            ["inbounds"] = new object[]
            {
                new
                {
                    type = "mixed",
                    tag = "mixed-in",
                    listen = "127.0.0.1",
                    listen_port = settings.MixedPort,
                    sniff = true,
                    sniff_override_destination = true,
                },
            },
        };

        var outbounds = new List<object>
        {
            new
            {
                type = "direct",
                tag = "direct",
            },
        };

        if (selected is not null)
        {
            outbounds.Insert(0, BuildProxyOutbound(selected));
        }

        root["outbounds"] = outbounds.ToArray();
        root["route"] = BuildRoute(settings, hasProxy);

        var experimental = new Dictionary<string, object?>();
        if (settings.EnableClashApi)
        {
            experimental["clash_api"] = new
            {
                external_controller = $"127.0.0.1:{settings.ClashApiPort}",
                secret = settings.ClashApiSecret ?? string.Empty,
            };
        }

        if (settings.EnableDirectCn && hasProxy)
        {
            experimental["cache_file"] = new { enabled = true };
        }

        if (experimental.Count > 0)
        {
            root["experimental"] = experimental;
        }

        return JsonSerializer.Serialize(root, JsonOptions);
    }

    public async Task WriteAsync(AppSettings settings, string configPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var json = Build(settings);
        await File.WriteAllTextAsync(configPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
    }

    private static object BuildRoute(AppSettings settings, bool hasProxy)
    {
        if (!hasProxy)
        {
            return new { final = "direct" };
        }

        if (!settings.EnableDirectCn)
        {
            return new { final = "proxy" };
        }

        return new
        {
            rules = new object[]
            {
                new { ip_is_private = true, outbound = "direct" },
                new { domain_suffix = new[] { ".cn" }, outbound = "direct" },
                new { rule_set = "geosite-cn", outbound = "direct" },
                new { rule_set = "geoip-cn", outbound = "direct" },
            },
            rule_set = new object[]
            {
                new
                {
                    tag = "geosite-cn",
                    type = "remote",
                    format = "binary",
                    url = "https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/geosite-cn.srs",
                    download_detour = "proxy",
                },
                new
                {
                    tag = "geoip-cn",
                    type = "remote",
                    format = "binary",
                    url = "https://raw.githubusercontent.com/SagerNet/sing-geoip/rule-set/geoip-cn.srs",
                    download_detour = "proxy",
                },
            },
            final = "proxy",
        };
    }

    private static string NormalizeLogLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
        {
            return "info";
        }

        return level.Trim().ToLowerInvariant() switch
        {
            "trace" => "trace",
            "debug" => "debug",
            "info" => "info",
            "warn" => "warn",
            "warning" => "warn",
            "error" => "error",
            "fatal" => "fatal",
            _ => "info",
        };
    }

    private static ProxyNode? ResolveSelectedNode(AppSettings settings)
    {
        var nodes = settings.Nodes;
        if (nodes is null || nodes.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(settings.SelectedNodeId))
        {
            return nodes.FirstOrDefault(n => string.Equals(n.Id, settings.SelectedNodeId, StringComparison.OrdinalIgnoreCase))
                ?? nodes.FirstOrDefault();
        }

        return nodes.FirstOrDefault();
    }

    private static object BuildProxyOutbound(ProxyNode node)
    {
        var baseOutbound = new Dictionary<string, object?>
        {
            ["tag"] = "proxy",
            ["server"] = node.Server,
            ["server_port"] = node.Port,
        };

        switch (node.Type)
        {
            case ProxyNodeType.Vless:
                baseOutbound["type"] = "vless";
                baseOutbound["uuid"] = node.Uuid ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(node.Flow))
                {
                    baseOutbound["flow"] = node.Flow;
                }

                ApplyTlsAndTransport(baseOutbound, node);
                return baseOutbound;

            case ProxyNodeType.Trojan:
                baseOutbound["type"] = "trojan";
                baseOutbound["password"] = node.Password ?? string.Empty;
                ApplyTlsAndTransport(baseOutbound, node);
                return baseOutbound;

            case ProxyNodeType.Vmess:
                baseOutbound["type"] = "vmess";
                baseOutbound["uuid"] = node.Uuid ?? string.Empty;
                baseOutbound["security"] = string.IsNullOrWhiteSpace(node.Security) ? "auto" : node.Security;
                if (node.AlterId is not null)
                {
                    baseOutbound["alter_id"] = node.AlterId.Value;
                }

                ApplyTlsAndTransport(baseOutbound, node);
                return baseOutbound;

            case ProxyNodeType.Shadowsocks:
                baseOutbound["type"] = "shadowsocks";
                baseOutbound["method"] = node.Method ?? "aes-128-gcm";
                baseOutbound["password"] = node.Password ?? string.Empty;
                return baseOutbound;

            default:
                baseOutbound["type"] = "direct";
                baseOutbound["tag"] = "direct";
                return baseOutbound;
        }
    }

    private static void ApplyTlsAndTransport(Dictionary<string, object?> outbound, ProxyNode node)
    {
        var isWs = string.Equals(node.TransportType, "ws", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(node.Security, "reality", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(node.RealityPublicKey))
        {
            var tls = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["server_name"] = node.TlsServerName ?? string.Empty,
                ["insecure"] = node.TlsInsecure,
                ["reality"] = new
                {
                    enabled = true,
                    public_key = node.RealityPublicKey ?? string.Empty,
                    short_id = node.RealityShortId ?? string.Empty,
                },
            };

            var alpn = SplitAlpn(node.TlsAlpn);
            if (alpn is not null)
            {
                tls["alpn"] = alpn;
            }
            else if (isWs)
            {
                tls["alpn"] = new[] { "http/1.1" };
            }

            if (!string.IsNullOrWhiteSpace(node.UtlsFingerprint))
            {
                tls["utls"] = new
                {
                    enabled = true,
                    fingerprint = node.UtlsFingerprint,
                };
            }

            outbound["tls"] = tls;
        }
        else if (node.TlsEnabled || !string.IsNullOrWhiteSpace(node.TlsServerName))
        {
            var tls = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["server_name"] = node.TlsServerName ?? string.Empty,
                ["insecure"] = node.TlsInsecure,
            };

            var alpn = SplitAlpn(node.TlsAlpn);
            if (alpn is not null)
            {
                tls["alpn"] = alpn;
            }
            else if (isWs)
            {
                tls["alpn"] = new[] { "http/1.1" };
            }

            if (!string.IsNullOrWhiteSpace(node.UtlsFingerprint))
            {
                tls["utls"] = new
                {
                    enabled = true,
                    fingerprint = node.UtlsFingerprint,
                };
            }

            outbound["tls"] = tls;
        }

        if (isWs)
        {
            var headers = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(node.TransportHost))
            {
                headers["Host"] = node.TransportHost;
            }
            else if (!string.IsNullOrWhiteSpace(node.TlsServerName))
            {
                headers["Host"] = node.TlsServerName;
            }

            var (path, maxEarlyData) = NormalizeWsPathAndEarlyData(node.TransportPath);
            outbound["transport"] = new
            {
                type = "ws",
                path = path,
                headers = headers.Count == 0 ? null : headers,
                max_early_data = maxEarlyData,
                early_data_header_name = maxEarlyData is > 0 ? "Sec-WebSocket-Protocol" : string.Empty,
            };
        }
        else if (string.Equals(node.TransportType, "grpc", StringComparison.OrdinalIgnoreCase))
        {
            outbound["transport"] = new
            {
                type = "grpc",
                service_name = string.IsNullOrWhiteSpace(node.TransportPath) ? "TunService" : node.TransportPath,
            };
        }
    }

    private static (string Path, int MaxEarlyData) NormalizeWsPathAndEarlyData(string? path)
    {
        var p = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
        if (!p.StartsWith('/'))
        {
            p = "/" + p;
        }

        var maxEarlyData = 0;
        var queryIndex = p.IndexOf('?');
        if (queryIndex >= 0)
        {
            var query = p[(queryIndex + 1)..];
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!part.StartsWith("ed=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = part["ed=".Length..];
                if (int.TryParse(value, out var ed) && ed > 0)
                {
                    maxEarlyData = ed;
                }
                break;
            }

            p = p[..queryIndex];
        }

        if (string.IsNullOrWhiteSpace(p))
        {
            p = "/";
        }

        return (p, maxEarlyData);
    }

    private static string[]? SplitAlpn(string? alpn)
    {
        if (string.IsNullOrWhiteSpace(alpn))
        {
            return null;
        }

        var parts = alpn
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0)
            .ToArray();

        return parts.Length == 0 ? null : parts;
    }
}
