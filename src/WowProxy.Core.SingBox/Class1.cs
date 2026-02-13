using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using WowProxy.Core.Abstractions;
using WowProxy.Domain;
using WowProxy.Infrastructure;

namespace WowProxy.Core.SingBox;

public sealed class SingBoxConfigFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public string Build(AppSettings settings)
    {
        var selected = ResolveSelectedNode(settings);

        var root = new Dictionary<string, object?>
        {
            ["log"] = new
            {
                level = "info",
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
        root["route"] = new
        {
            final = selected is not null ? "proxy" : "direct",
        };

        if (settings.EnableClashApi)
        {
            root["experimental"] = new
            {
                clash_api = new
                {
                    external_controller = $"127.0.0.1:{settings.ClashApiPort}",
                    secret = settings.ClashApiSecret ?? string.Empty,
                },
            };
        }

        return JsonSerializer.Serialize(root, JsonOptions);
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

        if (string.Equals(node.TransportType, "ws", StringComparison.OrdinalIgnoreCase))
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
        else
        {
            var edIndex = p.IndexOf("ed=", StringComparison.OrdinalIgnoreCase);
            if (edIndex >= 0)
            {
                var start = edIndex + 3;
                var end = start;
                while (end < p.Length && char.IsDigit(p[end]))
                {
                    end++;
                }

                if (end > start && int.TryParse(p[start..end], out var ed) && ed > 0)
                {
                    maxEarlyData = ed;
                }
            }
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

    public async Task WriteAsync(AppSettings settings, string configPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var json = Build(settings);
        await File.WriteAllTextAsync(configPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
    }
}

public sealed class SingBoxCoreAdapter : ICoreAdapter
{
    private readonly string _singBoxExePath;
    private readonly object _gate = new();
    private Process? _process;
    private CoreRuntimeInfo _runtimeInfo = new(CoreState.Stopped, null, null, null);

    public SingBoxCoreAdapter(string singBoxExePath)
    {
        _singBoxExePath = singBoxExePath;
    }

    public CoreRuntimeInfo RuntimeInfo
    {
        get
        {
            lock (_gate)
            {
                return _runtimeInfo;
            }
        }
    }

    public event EventHandler<CoreRuntimeInfo>? RuntimeInfoChanged;
    public event EventHandler<CoreLogLine>? LogReceived;

    public Task<string?> GetVersionAsync(CancellationToken cancellationToken = default)
        => RunOnceAsync("version", workingDirectory: Environment.CurrentDirectory, cancellationToken);

    public async Task<CoreCheckResult> CheckConfigAsync(string configPath, string workingDirectory, CancellationToken cancellationToken = default)
    {
        var (exitCode, stdout, stderr) = await RunOnceWithExitCodeAsync(
            $"check -c \"{configPath}\"",
            workingDirectory,
            cancellationToken
        );

        var ok = exitCode == 0;
        if (!ok && stderr.Contains("unknown command", StringComparison.OrdinalIgnoreCase) && stderr.Contains("check", StringComparison.OrdinalIgnoreCase))
        {
            ok = true;
        }

        return new CoreCheckResult(ok, exitCode, stdout, stderr);
    }

    public async Task StartAsync(CoreStartOptions options, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_runtimeInfo.State is CoreState.Running or CoreState.Starting)
            {
                return;
            }

            SetRuntimeInfoLocked(_runtimeInfo with { State = CoreState.Starting, LastError = null });
        }

        if (!File.Exists(_singBoxExePath))
        {
            SetRuntimeInfo(new CoreRuntimeInfo(CoreState.Faulted, null, null, $"找不到 sing-box：{_singBoxExePath}"));
            return;
        }

        if (!File.Exists(options.ConfigPath))
        {
            SetRuntimeInfo(new CoreRuntimeInfo(CoreState.Faulted, null, null, $"找不到配置文件：{options.ConfigPath}"));
            return;
        }

        Directory.CreateDirectory(options.WorkingDirectory);

        var extraArgs = options.ExtraArguments is { Count: > 0 }
            ? " " + string.Join(" ", options.ExtraArguments)
            : string.Empty;

        var psi = new ProcessStartInfo
        {
            FileName = _singBoxExePath,
            Arguments = $"run -c \"{options.ConfigPath}\"{extraArgs}",
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var process = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true,
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            EmitLog(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            EmitLog(e.Data);
        };

        process.Exited += (_, _) =>
        {
            var exitCode = 0;
            try
            {
                exitCode = process.ExitCode;
            }
            catch
            {
            }

            lock (_gate)
            {
                if (!ReferenceEquals(_process, process))
                {
                    return;
                }

                _process = null;
            }

            SetRuntimeInfo(new CoreRuntimeInfo(CoreState.Faulted, null, null, $"sing-box 已退出（exit={exitCode}）"));
        };

        try
        {
            _ = process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            lock (_gate)
            {
                _process = process;
            }

            SetRuntimeInfo(new CoreRuntimeInfo(CoreState.Running, process.Id, DateTimeOffset.Now, null));
        }
        catch (Exception ex)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            SetRuntimeInfo(new CoreRuntimeInfo(CoreState.Faulted, null, null, ex.Message));
        }

        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;

            if (_runtimeInfo.State is CoreState.Stopped)
            {
                return;
            }

            SetRuntimeInfoLocked(_runtimeInfo with { State = CoreState.Stopping });
        }

        if (process is null)
        {
            SetRuntimeInfo(new CoreRuntimeInfo(CoreState.Stopped, null, null, null));
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
        }

        SetRuntimeInfo(new CoreRuntimeInfo(CoreState.Stopped, null, null, null));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private void SetRuntimeInfo(CoreRuntimeInfo info)
    {
        lock (_gate)
        {
            SetRuntimeInfoLocked(info);
        }
    }

    private void SetRuntimeInfoLocked(CoreRuntimeInfo info)
    {
        _runtimeInfo = info;
        RuntimeInfoChanged?.Invoke(this, info);
    }

    private void EmitLog(string line)
    {
        var level = line.Contains("FATAL", StringComparison.OrdinalIgnoreCase) || line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
            ? CoreLogLevel.Error
            : line.Contains("WARN", StringComparison.OrdinalIgnoreCase)
                ? CoreLogLevel.Warning
                : CoreLogLevel.Info;

        LogReceived?.Invoke(this, new CoreLogLine(DateTimeOffset.Now, level, line));
    }

    private async Task<string?> RunOnceAsync(string arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var (exitCode, stdout, _) = await RunOnceWithExitCodeAsync(arguments, workingDirectory, cancellationToken);
        return exitCode == 0 ? stdout.Trim() : null;
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunOnceWithExitCodeAsync(
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken
    )
    {
        if (!File.Exists(_singBoxExePath))
        {
            return (-1, string.Empty, $"找不到 sing-box：{_singBoxExePath}");
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var psi = new ProcessStartInfo
        {
            FileName = _singBoxExePath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        _ = p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(cancellationToken);

        return (p.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
