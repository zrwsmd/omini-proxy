using System.Diagnostics;
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
            ["outbounds"] = new object[]
            {
                new
                {
                    type = "direct",
                    tag = "direct",
                },
            },
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
