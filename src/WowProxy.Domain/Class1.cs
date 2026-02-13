namespace WowProxy.Domain;

public static class AppRuntime
{
    public static string? TunInterfaceName { get; set; }
}

public sealed record AppSettings(
    string? SingBoxPath,
    int MixedPort,
    bool EnableClashApi,
    int ClashApiPort,
    string? ClashApiSecret,
    bool EnableSystemProxy,
    string? SubscriptionUrl = null,
    List<ProxyNode>? Nodes = null,
    string? SelectedNodeId = null,
    string LogLevel = "info",
    bool EnableDirectCn = true,
    bool EnableTun = false,
    string? TunInterfaceName = null
)
{
    public static AppSettings Default =>
        new(
            SingBoxPath: null,
            MixedPort: 10808,
            EnableClashApi: false,
            ClashApiPort: 9090,
            ClashApiSecret: null,
            EnableSystemProxy: false,
            SubscriptionUrl: null,
            Nodes: null,
            SelectedNodeId: null,
            LogLevel: "info",
            EnableDirectCn: true,
            EnableTun: false,
            TunInterfaceName: null
        );
}
