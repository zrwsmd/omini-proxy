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
        var chainNodes = ResolveChainNodes(settings);
        var selected = chainNodes.Count > 0 ? chainNodes.Last() : ResolveSelectedNode(settings);
        var hasProxy = selected is not null;
        var logLevel = NormalizeLogLevel(settings.LogLevel);

        // For TUN route exclusion, use the first hop (the one we connect to directly)
        var firstHopNode = chainNodes.Count > 0 ? chainNodes.First() : selected;

        var root = new Dictionary<string, object?>
        {
            ["log"] = new
            {
                level = logLevel,
                timestamp = true,
            },
            ["inbounds"] = BuildInbounds(settings, firstHopNode),
        };

        var outbounds = new List<object>
        {
            new
            {
                type = "direct",
                tag = "direct",
            },
        };

        if (chainNodes.Count >= 2)
        {
            // Chain proxy mode: build linked outbounds via detour
            outbounds.InsertRange(0, BuildChainOutbounds(chainNodes));
        }
        else if (selected is not null)
        {
            outbounds.Insert(0, BuildProxyOutbound(selected));
        }

        root["outbounds"] = outbounds.ToArray();
        root["route"] = BuildRoute(settings, hasProxy);

        if (settings.EnableTun && hasProxy)
        {
            root["dns"] = BuildTunDns(firstHopNode, chainNodes);
        }

        var experimental = new Dictionary<string, object?>();
        
        // 始终开启 Clash API 以支持实时连接监控
        // 如果用户未自定义端口，则使用默认的 9090
        var clashApiPort = settings.ClashApiPort > 0 ? settings.ClashApiPort : 9090;
        experimental["clash_api"] = new
        {
            external_controller = $"127.0.0.1:{clashApiPort}",
            secret = settings.ClashApiSecret ?? string.Empty,
        };

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
            var route = new Dictionary<string, object?>
            {
                ["final"] = "direct",
            };

            if (settings.EnableTun)
            {
                route["auto_detect_interface"] = true;
            }

            return route;
        }

        if (!settings.EnableDirectCn)
        {
            var route = new Dictionary<string, object?>
            {
                ["final"] = "proxy",
            };

            if (settings.EnableTun)
            {
                route["auto_detect_interface"] = true;
                route["default_domain_resolver"] = new { server = "dns-direct" };

                // 强制劫持 DNS 流量
                route["rules"] = new object[]
                {
                    new { protocol = "dns", action = "hijack-dns" },
                    new { port = 53, action = "hijack-dns" }
                };
            }

            var bypassRules = BuildBypassTunRules(settings);
            if (bypassRules.Length > 0)
            {
                var allRules = route.ContainsKey("rules") ? ((object[])route["rules"]!).ToList() : new List<object>();
                allRules.InsertRange(0, bypassRules);
                route["rules"] = allRules.ToArray();
            }

            return route;
        }

        var cnRoute = new Dictionary<string, object?>
        {
            ["rules"] = new object[]
            {
                new { ip_is_private = true, outbound = "direct" },
                new { domain_suffix = new[] { ".cn" }, outbound = "direct" },
                new { rule_set = "geosite-cn", outbound = "direct" },
                new { rule_set = "geoip-cn", outbound = "direct" },
                new { domain_suffix = new[] { "orchids.app", "posthog.com", "supabase.co" }, outbound = "direct" },
            },
            ["rule_set"] = new object[]
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
            ["final"] = "proxy",
        };

        if (settings.EnableTun)
        {
            cnRoute["auto_detect_interface"] = true;
            cnRoute["default_domain_resolver"] = new { server = "dns-direct" };

            // 强制劫持 DNS 流量，防止系统 DNS 设置为 127.0.0.1 时的回环拒绝问题
            var rules = (object[])cnRoute["rules"]!;
            var newRules = new List<object>
            {
                new { protocol = "dns", action = "hijack-dns" },
                new { port = 53, action = "hijack-dns" }
            };
            newRules.AddRange(rules);
            cnRoute["rules"] = newRules.ToArray();
        }

        var bypassRules2 = BuildBypassTunRules(settings);
        if (bypassRules2.Length > 0)
        {
            var allRules = cnRoute.ContainsKey("rules") ? ((object[])cnRoute["rules"]!).ToList() : new List<object>();
            allRules.InsertRange(0, bypassRules2);
            cnRoute["rules"] = allRules.ToArray();
        }

        return cnRoute;
    }

    /// <summary>
    /// 为进程生成路由规则：
    /// 1. 强制占位规则，使 sing-box 对所有连接开启进程匹配（Dashboard 展示进程名所必需）。
    /// 2. 为用户配置的白名单进程生成规则：将被 TUN 捕获的所有流量直连（不经过代理节点）。
    /// </summary>
    private static object[] BuildBypassTunRules(AppSettings settings)
    {
        var rules = new List<object>();

        // 核心机制：在路由规则中添加一个涉及 process_name 的规则，这会强制 sing-box 对连接进行进程溯源（Process Matching）。
        // 如果没有任何路由规则涉及进程名，Dashboard 的“进程”列将显示为空。
        // 此占位规则不限制入站（inbound），以便同时修复 TUN 和 Mixed (系统代理) 入站的进程名显示问题。
        // 由于使用了不存且带有非法字符的名称，它永远不会真正匹配到任何进程，仅用于触发 sing-box 的溯源逻辑。
        rules.Add(new
        {
            process_name = new[] { "placeholder-to-force-process-matching-for-dashboard.exe" },
            outbound = "direct"
        });

        var bypassProcesses = settings.BypassTunProcesses?
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        if (bypassProcesses is not null && bypassProcesses.Length > 0)
        {
            // 对于用户明确填写的直连进程，维持原有设计逻辑：
            // 仅对被 TUN 捕获的流量直连（inbound=tun-in），若走系统代理（mixed-in）则仍然正常代理。
            rules.Add(new
            {
                process_name = bypassProcesses,
                inbound = new[] { "tun-in" },
                outbound = "direct"
            });
        }

        return rules.ToArray();
    }

    private static object BuildTunDns(ProxyNode? proxyNode = null, List<ProxyNode>? chainNodes = null)
    {
        var dnsRules = new List<object>
        {
            new { rule_set = "geosite-cn", server = "dns-direct" },
            new { rule_set = "geoip-cn", server = "dns-direct" },
        };

        // 关键修复：如果代理节点的服务器地址是域名（不是 IP），
        // 必须使用直连 DNS 解析它，否则会出现 DNS 解析死循环：
        // 解析代理域名 → 用远程 DNS → 远程 DNS 走代理 → 代理没连上 → 💀
        // 链式代理时，所有链中节点的域名都必须走直连 DNS
        var directDnsDomains = new List<string>();

        if (chainNodes is { Count: >= 2 })
        {
            foreach (var node in chainNodes)
            {
                if (!string.IsNullOrWhiteSpace(node.Server) && !System.Net.IPAddress.TryParse(node.Server, out _))
                    directDnsDomains.Add(node.Server);
            }
        }
        else if (proxyNode != null && !System.Net.IPAddress.TryParse(proxyNode.Server, out _))
        {
            directDnsDomains.Add(proxyNode.Server);
        }

        if (directDnsDomains.Count > 0)
        {
            dnsRules.Insert(0, new { domain = directDnsDomains.Distinct().ToArray(), server = "dns-direct" });
        }

        return new
        {
            servers = new object[]
            {
                new
                {
                    type = "https",
                    tag = "dns-remote",
                    server = "1.1.1.1",
                    path = "/dns-query",
                    detour = "proxy",
                },
                new
                {
                    type = "udp",
                    tag = "dns-direct",
                    server = "223.5.5.5",
                    server_port = 53,
                },
                new
                {
                    type = "udp",
                    tag = "dns-local",
                    server = "223.5.5.5",
                    server_port = 53,
                },
            },
            rules = dnsRules.ToArray(),
            final = "dns-remote",
            strategy = "prefer_ipv4",
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

    /// <summary>
    /// Resolve the chain proxy node list from settings.
    /// Returns an ordered list of ProxyNode for the chain, or empty if chain proxy is disabled/invalid.
    /// </summary>
    private static List<ProxyNode> ResolveChainNodes(AppSettings settings)
    {
        if (!settings.EnableChainProxy
            || settings.ChainProxyNodeIds is null
            || settings.ChainProxyNodeIds.Count < 2
            || settings.Nodes is null)
        {
            return new List<ProxyNode>();
        }

        var nodeMap = settings.Nodes.ToDictionary(n => n.Id, n => n, StringComparer.OrdinalIgnoreCase);
        var result = new List<ProxyNode>();

        foreach (var id in settings.ChainProxyNodeIds)
        {
            if (nodeMap.TryGetValue(id, out var node))
                result.Add(node);
        }

        // Need at least 2 valid nodes for a chain
        return result.Count >= 2 ? result : new List<ProxyNode>();
    }

    /// <summary>
    /// Build a chain of outbounds linked via detour.
    /// Chain order: [0]=first hop (direct internet), [1]=second hop, ... [N-1]=exit node.
    /// The exit node gets tag "proxy". Each previous node gets "chain-hop-0", "chain-hop-1", etc.
    /// Traffic flow: local → chain-hop-0 → chain-hop-1 → ... → proxy → target
    /// </summary>
    private static List<object> BuildChainOutbounds(List<ProxyNode> chainNodes)
    {
        var result = new List<object>();

        for (int i = chainNodes.Count - 1; i >= 0; i--)
        {
            var node = chainNodes[i];
            bool isExitNode = (i == chainNodes.Count - 1);
            bool isFirstHop = (i == 0);
            string tag = isExitNode ? "proxy" : $"chain-hop-{i}";
            string? detour = isFirstHop ? null : (i == 1 ? "chain-hop-0" : $"chain-hop-{i - 1}");

            result.Add(BuildProxyOutbound(node, tag, detour));
        }

        return result;
    }

    private static object BuildProxyOutbound(ProxyNode node, string tag = "proxy", string? detour = null)
    {
        var baseOutbound = new Dictionary<string, object?>
        {
            ["tag"] = tag,
            ["server"] = node.Server,
            ["server_port"] = node.Port,
        };

        if (!string.IsNullOrWhiteSpace(detour))
        {
            baseOutbound["detour"] = detour;
        }

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

    private static object[] BuildInbounds(AppSettings settings, ProxyNode? selected)
    {
        var list = new List<object>
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
        };

        if (settings.EnableTun)
        {
            var interfaceName = string.IsNullOrWhiteSpace(settings.TunInterfaceName)
                ? string.Empty
                : settings.TunInterfaceName!.Trim();

            var hasBypassProcesses = settings.BypassTunProcesses?
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(x => !string.IsNullOrWhiteSpace(x)) == true;

            list.Add(new
            {
                type = "tun",
                tag = "tun-in",
                interface_name = interfaceName,
                address = new[] { "172.19.0.1/30" },
                mtu = 1500,
                auto_route = true,
                strict_route = !hasBypassProcesses,
                route_exclude_address = BuildTunRouteExcludeAddress(selected),
                stack = "system",
                sniff = true,
                sniff_override_destination = true,
            });
        }

        return list.ToArray();
    }

    private static string[] BuildTunRouteExcludeAddress(ProxyNode? selected)
    {
        var list = new List<string>
        {
            "0.0.0.0/8",
            "10.0.0.0/8",
            "100.64.0.0/10",
            "127.0.0.0/8",
            "169.254.0.0/16",
            "172.16.0.0/12",
            "192.168.0.0/16",
            "224.0.0.0/4",
            "240.0.0.0/4",
        };

        if (selected is not null && !string.IsNullOrWhiteSpace(selected.Server))
        {
            var host = selected.Server.Trim();
            if (System.Net.IPAddress.TryParse(host, out var ip))
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    list.Add($"{ip}/32");
                }
                else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                {
                    list.Add($"{ip}/128");
                }
            }
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
