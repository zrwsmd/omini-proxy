namespace WowProxy.Core.Abstractions;

public enum CoreState
{
    Stopped = 0,
    Starting = 1,
    Running = 2,
    Stopping = 3,
    Faulted = 4,
}

public enum CoreLogLevel
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

public sealed record CoreRuntimeInfo(
    CoreState State,
    int? ProcessId,
    DateTimeOffset? StartedAt,
    string? LastError
);

public sealed record CoreLogLine(
    DateTimeOffset Timestamp,
    CoreLogLevel Level,
    string Line
);

public sealed record CoreCheckResult(
    bool IsOk,
    int ExitCode,
    string Stdout,
    string Stderr
);

public sealed record CoreStartOptions(
    string WorkingDirectory,
    string ConfigPath,
    IReadOnlyList<string>? ExtraArguments = null
);

public interface ICoreAdapter : IAsyncDisposable
{
    CoreRuntimeInfo RuntimeInfo { get; }

    event EventHandler<CoreRuntimeInfo>? RuntimeInfoChanged;
    event EventHandler<CoreLogLine>? LogReceived;

    Task<string?> GetVersionAsync(CancellationToken cancellationToken = default);
    Task<CoreCheckResult> CheckConfigAsync(string configPath, string workingDirectory, CancellationToken cancellationToken = default);
    Task StartAsync(CoreStartOptions options, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
