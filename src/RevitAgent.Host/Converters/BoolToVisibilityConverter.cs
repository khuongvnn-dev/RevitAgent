namespace RevitAgent.Host.Converters
{
    using System;
    using System.Globalization;
    using System.Windows;
    using System.Windows.Data;

    public class BoolToVisibilityConverter : IValueConverter
    {
        public bool Inverted { get; set; }

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool val = value is bool b && b;
            if (Inverted) val = !val;
            return val ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is Visibility v)
            {
                bool isVisible = v == Visibility.Visible;
                return Inverted ? !isVisible : isVisible;
            }
            return false;
        }
    }
}
