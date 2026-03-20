using System;
using System.Globalization;
using System.Windows.Data;
using WowProxy.Domain;

namespace WowProxy.App.Converters;

public class RuleActionDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is RuleAction ruleAction)
        {
            return ruleAction switch
            {
                RuleAction.Proxy => "代理",
                RuleAction.Direct => "直连",
                RuleAction.Block => "拦截",
                _ => ruleAction.ToString()
            };
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
