namespace RevitAgent.Host.Converters
{
    using System;
    using System.Globalization;
    using System.Windows;
    using System.Windows.Data;
    using System.Windows.Media;

    /// <summary>
    /// Converts a boolean value to a Brush, supporting DynamicResource bindings via DependencyProperties.
    /// Derives from Freezable to enable dynamic theme updates in WPF ResourceDictionaries.
    /// </summary>
    public class BoolToBrushConverter : Freezable, IValueConverter
    {
        public static readonly DependencyProperty TrueBrushProperty =
            DependencyProperty.Register(nameof(TrueBrush), typeof(Brush), typeof(BoolToBrushConverter),
                new PropertyMetadata(Brushes.Green));

        public static readonly DependencyProperty FalseBrushProperty =
            DependencyProperty.Register(nameof(FalseBrush), typeof(Brush), typeof(BoolToBrushConverter),
                new PropertyMetadata(Brushes.Red));

        public Brush TrueBrush
        {
            get => (Brush)GetValue(TrueBrushProperty);
            set => SetValue(TrueBrushProperty, value);
        }

        public Brush FalseBrush
        {
            get => (Brush)GetValue(FalseBrushProperty);
            set => SetValue(FalseBrushProperty, value);
        }

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b)
            {
                return b ? TrueBrush : FalseBrush;
            }
            return FalseBrush;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }

        protected override Freezable CreateInstanceCore() => new BoolToBrushConverter();
    }
}
