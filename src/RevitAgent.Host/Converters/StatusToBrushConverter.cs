namespace RevitAgent.Host.Converters
{
    using System;
    using System.Globalization;
    using System.Windows.Data;
    using System.Windows.Media;
    using RevitAgent.Host.Services;

    public class StatusToBrushConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string category)
            {
                return AuditLogService.GetBadgeColor(category);
            }
            return new SolidColorBrush(Color.FromRgb(71, 85, 105));
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
