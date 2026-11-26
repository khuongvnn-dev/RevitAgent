namespace RevitAgent.Host.Converters
{
    using System;
    using System.Globalization;
    using System.Windows;
    using System.Windows.Data;

    /// <summary>Visible when a collection count is greater than zero (or, when <see cref="Inverted"/>, when it is zero): empty-state hints.</summary>
    public class CountToVisibilityConverter : IValueConverter
    {
        public bool Inverted { get; set; }

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool hasItems = value is int count && count > 0;
            return hasItems != Inverted ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
