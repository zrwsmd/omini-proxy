using System;
using System.Globalization;
using System.Windows.Data;
using WowProxy.Domain;

namespace WowProxy.App.Converters;

public class RuleTypeDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is RuleType ruleType)
        {
            return ruleType switch
            {
                RuleType.DomainSuffix => "域名后缀 (如：.google.com)",
                RuleType.Domain => "域名精确 (如：www.google.com)",
                RuleType.DomainKeyword => "域名关键词 (如：google)",
                RuleType.IpCidr => "IP 段 (如：1.2.3.0/24)",
                RuleType.GeoIp => "GeoIP (如：US、JP、CN)",
                RuleType.ProcessName => "进程名 (如：chrome.exe)",
                RuleType.Port => "端口 (如：443)",
                RuleType.PortRange => "端口范围 (如：8000:9000)",
                RuleType.Network => "网络协议 (tcp 或 udp)",
                _ => ruleType.ToString()
            };
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
