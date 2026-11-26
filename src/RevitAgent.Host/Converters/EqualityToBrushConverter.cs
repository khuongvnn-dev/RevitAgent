namespace RevitAgent.Host.Converters
{
    using System;
    using System.Globalization;
    using System.Windows.Data;
    using System.Windows.Media;

    public class EqualityToBrushConverter : IValueConverter
    {
        public Brush ActiveBrush { get; set; } = new SolidColorBrush(Color.FromRgb(59, 130, 246));
        public Brush InactiveBrush { get; set; } = new SolidColorBrush(Color.FromRgb(30, 41, 59));
        public Brush ActiveForeground { get; set; } = Brushes.White;
        public Brush InactiveForeground { get; set; } = new SolidColorBrush(Color.FromRgb(148, 163, 184));
        public bool ReturnForeground { get; set; }

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool isEqual = string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

            if (ReturnForeground)
            {
                return isEqual ? ActiveForeground : InactiveForeground;
            }

            return isEqual ? ActiveBrush : InactiveBrush;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
