using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace WowProxy.App.Converters;

public class StatusColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string status)
        {
            if (status.Contains("运行中"))
                return new SolidColorBrush(Color.FromRgb(34, 139, 34)); // ForestGreen
            if (status.Contains("启动中"))
                return Brushes.Orange;
            if (status.Contains("异常") || status.Contains("失败") || status.Contains("被占用"))
                return Brushes.Red;
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
