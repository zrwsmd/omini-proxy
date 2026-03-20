using System.ComponentModel;
using System.Runtime.CompilerServices;
using WowProxy.Domain;

namespace WowProxy.App.Models;

public class UserRuleModel : INotifyPropertyChanged
{
    private bool _enabled;

    public UserRuleModel(UserRule rule)
    {
        Rule = rule;
        _enabled = rule.Enabled;
    }

    public UserRule Rule { get; private set; }

    public string Id => Rule.Id;
    public RuleType Type => Rule.Type;
    public string Value => Rule.Value;
    public RuleAction Action => Rule.Action;
    public string? Remark => Rule.Remark;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled != value)
            {
                _enabled = value;
                Rule = Rule with { Enabled = value };
                OnPropertyChanged();
            }
        }
    }

    public string TypeDisplay => Type switch
    {
        RuleType.DomainSuffix => "域名后缀",
        RuleType.Domain => "域名精确",
        RuleType.DomainKeyword => "域名关键词",
        RuleType.IpCidr => "IP 段",
        RuleType.GeoIp => "GeoIP",
        RuleType.ProcessName => "进程名",
        RuleType.Port => "端口",
        RuleType.PortRange => "端口范围",
        RuleType.Network => "网络协议",
        _ => Type.ToString()
    };

    public string ActionDisplay => Action switch
    {
        RuleAction.Proxy => "代理",
        RuleAction.Direct => "直连",
        RuleAction.Block => "拦截",
        _ => Action.ToString()
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
