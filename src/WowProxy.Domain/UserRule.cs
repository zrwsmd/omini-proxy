namespace WowProxy.Domain;

public enum RuleType
{
    DomainSuffix,
    Domain,
    DomainKeyword,
    IpCidr,
    GeoIp,
    ProcessName,
    Port,
    PortRange,
    Network
}

public enum RuleAction
{
    Proxy,
    Direct,
    Block
}

public record UserRule(
    string Id,
    RuleType Type,
    string Value,
    RuleAction Action,
    bool Enabled,
    string? Remark = null
)
{
    public static UserRule Create(RuleType type, string value, RuleAction action, string? remark = null)
    {
        return new UserRule(
            Id: Guid.NewGuid().ToString("N"),
            Type: type,
            Value: value,
            Action: action,
            Enabled: true,
            Remark: remark
        );
    }
}
