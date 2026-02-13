namespace WowProxy.Domain;

public sealed record AppSettings(
    string? SingBoxPath,
    int MixedPort,
    bool EnableClashApi,
    int ClashApiPort,
    string? ClashApiSecret,
    bool EnableSystemProxy
)
{
    public static AppSettings Default =>
        new(
            SingBoxPath: null,
            MixedPort: 10808,
            EnableClashApi: false,
            ClashApiPort: 9090,
            ClashApiSecret: null,
            EnableSystemProxy: false
        );
}
