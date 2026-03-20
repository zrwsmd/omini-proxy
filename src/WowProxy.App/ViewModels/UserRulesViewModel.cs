using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using WowProxy.App.Models;
using WowProxy.Domain;

namespace WowProxy.App.ViewModels;

public class UserRulesViewModel : INotifyPropertyChanged
{
    private readonly ObservableCollection<UserRuleModel> _rules = new();
    private RuleType _newType = RuleType.DomainSuffix;
    private string _newValue = string.Empty;
    private RuleAction _newAction = RuleAction.Proxy;
    private string _newRemark = string.Empty;
    private string _validationError = string.Empty;
    private UserRuleModel? _selectedRule;
    private readonly Action? _onRulesChanged;

    public UserRulesViewModel(List<UserRule>? initialRules = null, Action? onRulesChanged = null)
    {
        _onRulesChanged = onRulesChanged;
        
        if (initialRules != null)
        {
            foreach (var rule in initialRules)
            {
                _rules.Add(new UserRuleModel(rule, onRulesChanged));
            }
        }

        AddRuleCommand = new RelayCommand(_ => AddRule(), _ => CanAddRule());
        DeleteRuleCommand = new RelayCommand(_ => DeleteRule());
        MoveUpCommand = new RelayCommand(_ => MoveUp());
        MoveDownCommand = new RelayCommand(_ => MoveDown());
    }

    public ObservableCollection<UserRuleModel> Rules => _rules;

    public RuleType NewType
    {
        get => _newType;
        set
        {
            if (_newType != value)
            {
                _newType = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NewValueHint));
                ValidateNewValue();
            }
        }
    }

    public string NewValue
    {
        get => _newValue;
        set
        {
            if (_newValue != value)
            {
                _newValue = value;
                OnPropertyChanged();
                ValidateNewValue();
                AddRuleCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RuleAction NewAction
    {
        get => _newAction;
        set
        {
            if (_newAction != value)
            {
                _newAction = value;
                OnPropertyChanged();
            }
        }
    }

    public string NewRemark
    {
        get => _newRemark;
        set
        {
            if (_newRemark != value)
            {
                _newRemark = value;
                OnPropertyChanged();
            }
        }
    }

    public string ValidationError
    {
        get => _validationError;
        private set
        {
            if (_validationError != value)
            {
                _validationError = value;
                OnPropertyChanged();
                AddRuleCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public UserRuleModel? SelectedRule
    {
        get => _selectedRule;
        set
        {
            if (_selectedRule != value)
            {
                _selectedRule = value;
                OnPropertyChanged();
            }
        }
    }

    public string NewValueHint => NewType switch
    {
        RuleType.DomainSuffix => "如：.google.com",
        RuleType.Domain => "如：www.google.com",
        RuleType.DomainKeyword => "如：google",
        RuleType.IpCidr => "如：1.2.3.0/24 或 2001:db8::/32",
        RuleType.GeoIp => "如：US、JP、CN（2位大写国家代码）",
        RuleType.ProcessName => "如：chrome.exe",
        RuleType.Port => "如：443",
        RuleType.PortRange => "如：8000:9000",
        RuleType.Network => "tcp 或 udp",
        _ => string.Empty
    };

    public RelayCommand AddRuleCommand { get; }
    public RelayCommand DeleteRuleCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }

    private bool CanAddRule()
    {
        return string.IsNullOrWhiteSpace(ValidationError) && !string.IsNullOrWhiteSpace(NewValue);
    }

    private void AddRule()
    {
        if (!CanAddRule()) return;

        var rule = UserRule.Create(NewType, NewValue.Trim(), NewAction, 
            string.IsNullOrWhiteSpace(NewRemark) ? null : NewRemark.Trim());
        _rules.Add(new UserRuleModel(rule, _onRulesChanged));

        // Clear form
        NewValue = string.Empty;
        NewRemark = string.Empty;
        ValidationError = string.Empty;
        
        _onRulesChanged?.Invoke();
    }

    private void DeleteRule()
    {
        if (SelectedRule != null)
        {
            _rules.Remove(SelectedRule);
            SelectedRule = null;
            _onRulesChanged?.Invoke();
        }
    }

    private void MoveUp()
    {
        if (SelectedRule == null) return;
        var index = _rules.IndexOf(SelectedRule);
        if (index > 0)
        {
            _rules.Move(index, index - 1);
            _onRulesChanged?.Invoke();
        }
    }

    private void MoveDown()
    {
        if (SelectedRule == null) return;
        var index = _rules.IndexOf(SelectedRule);
        if (index >= 0 && index < _rules.Count - 1)
        {
            _rules.Move(index, index + 1);
            _onRulesChanged?.Invoke();
        }
    }

    private void ValidateNewValue()
    {
        var value = NewValue?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            ValidationError = string.Empty;
            return;
        }

        ValidationError = NewType switch
        {
            RuleType.IpCidr => ValidateIpCidr(value),
            RuleType.Port => ValidatePort(value),
            RuleType.PortRange => ValidatePortRange(value),
            RuleType.GeoIp => ValidateGeoIp(value),
            RuleType.ProcessName => ValidateProcessName(value),
            RuleType.Network => ValidateNetwork(value),
            _ => string.Empty
        };
    }

    private static string ValidateIpCidr(string value)
    {
        var parts = value.Split('/');
        if (parts.Length != 2)
            return "格式错误，应为 IP/前缀长度";

        if (!IPAddress.TryParse(parts[0], out var ip))
            return "IP 地址格式无效";

        if (!int.TryParse(parts[1], out var prefix))
            return "前缀长度必须是数字";

        var maxPrefix = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (prefix < 0 || prefix > maxPrefix)
            return $"前缀长度必须在 0-{maxPrefix} 之间";

        return string.Empty;
    }

    private static string ValidatePort(string value)
    {
        if (!int.TryParse(value, out var port) || port < 1 || port > 65535)
            return "端口必须是 1-65535 之间的整数";
        return string.Empty;
    }

    private static string ValidatePortRange(string value)
    {
        var parts = value.Split(':');
        if (parts.Length != 2)
            return "格式错误，应为 起始端口:结束端口";

        if (!int.TryParse(parts[0], out var start) || start < 1 || start > 65535)
            return "起始端口必须是 1-65535 之间的整数";

        if (!int.TryParse(parts[1], out var end) || end < 1 || end > 65535)
            return "结束端口必须是 1-65535 之间的整数";

        if (start >= end)
            return "起始端口必须小于结束端口";

        return string.Empty;
    }

    private static string ValidateGeoIp(string value)
    {
        if (value.Length != 2 || !Regex.IsMatch(value, "^[A-Z]{2}$"))
            return "必须是 2 位大写字母的 ISO 国家代码";
        return string.Empty;
    }

    private static string ValidateProcessName(string value)
    {
        if (!value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return "进程名必须以 .exe 结尾";
        return string.Empty;
    }

    private static string ValidateNetwork(string value)
    {
        var lower = value.ToLowerInvariant();
        if (lower != "tcp" && lower != "udp")
            return "必须是 tcp 或 udp";
        return string.Empty;
    }

    public List<UserRule> GetRules()
    {
        return _rules.Select(m => m.Rule).ToList();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
