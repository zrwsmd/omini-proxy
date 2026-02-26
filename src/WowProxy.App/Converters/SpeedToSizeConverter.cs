using System;
using System.Globalization;
using System.Windows.Data;

namespace WowProxy.App.Converters;

public class SpeedToSizeConverter : IValueConverter
{
    public double MinSize { get; set; } = 20;
    public double MaxSize { get; set; } = 80;
    
    // Scale speed (in bytes/sec) to Size
    // 0 bytes -> MinSize
    // 10MB/s -> MaxSize
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is long speed)
        {
            if (speed <= 0) return MinSize;
            
            // logarithmic scale to make small speeds visible but not let big speeds explode
            var logSpeed = Math.Log10(speed); 
            // 10MB/s is approx 10^7 (log=7)
            // 1KB/s is 10^3 (log=3)
            
            var normalized = Math.Max(0, (logSpeed - 2) / 5.0); // scale 100B - 10MB
            var size = MinSize + (normalized * (MaxSize - MinSize));
            
            return Math.Min(MaxSize, Math.Max(MinSize, size));
        }
        return MinSize;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
