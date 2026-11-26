namespace RevitAgent.Host.Controls
{
    using System;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Data;
    using System.Windows.Media;
    using System.Windows.Shapes;

    /// <summary>
    /// Resilient Icon control that prioritizes PNG mock icon assets if present on disk,
    /// and gracefully falls back to crisp vector StreamGeometry or text glyph.
    /// Eliminates layout shift by locking fixed dimensions.
    /// </summary>
    public class IconPresenter : ContentControl
    {
        public static readonly DependencyProperty IconNameProperty =
            DependencyProperty.Register(nameof(IconName), typeof(string), typeof(IconPresenter),
                new PropertyMetadata(null, OnPropertyChangedStatic));

        public static readonly DependencyProperty FallbackGlyphProperty =
            DependencyProperty.Register(nameof(FallbackGlyph), typeof(string), typeof(IconPresenter),
                new PropertyMetadata(null, OnPropertyChangedStatic));

        public static readonly DependencyProperty FallbackGeometryProperty =
            DependencyProperty.Register(nameof(FallbackGeometry), typeof(Geometry), typeof(IconPresenter),
                new PropertyMetadata(null, OnPropertyChangedStatic));

        public static readonly DependencyProperty IconSizeProperty =
            DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(IconPresenter),
                new PropertyMetadata(16.0, OnPropertyChangedStatic));

        public string? IconName
        {
            get => (string?)GetValue(IconNameProperty);
            set => SetValue(IconNameProperty, value);
        }

        public string? FallbackGlyph
        {
            get => (string?)GetValue(FallbackGlyphProperty);
            set => SetValue(FallbackGlyphProperty, value);
        }

        public Geometry? FallbackGeometry
        {
            get => (Geometry?)GetValue(FallbackGeometryProperty);
            set => SetValue(FallbackGeometryProperty, value);
        }

        public double IconSize
        {
            get => (double)GetValue(IconSizeProperty);
            set => SetValue(IconSizeProperty, value);
        }

        public IconPresenter()
        {
            IsTabStop = false;
            Focusable = false;
            VerticalAlignment = VerticalAlignment.Center;
            HorizontalAlignment = HorizontalAlignment.Center;
            Loaded += (s, e) => UpdateContent();
        }

        private static void OnPropertyChangedStatic(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is IconPresenter presenter)
            {
                presenter.UpdateContent();
            }
        }

        public void UpdateContent()
        {
            double size = IconSize > 0 ? IconSize : 16.0;
            Width = size;
            Height = size;

            // 1. Try loading PNG icon first
            if (!string.IsNullOrWhiteSpace(IconName))
            {
                var bitmap = IconLoader.GetIcon(IconName);
                if (bitmap != null)
                {
                    Content = new Image
                    {
                        Source = bitmap,
                        Width = size,
                        Height = size,
                        Stretch = Stretch.Uniform,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center
                    };
                    return;
                }
            }

            // 2. Fall back to Vector StreamGeometry
            if (FallbackGeometry != null)
            {
                var path = new Path
                {
                    Data = FallbackGeometry,
                    Width = Math.Max(size - 2, 8),
                    Height = Math.Max(size - 2, 8),
                    Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                // Bind Path Fill to Foreground (or BrushTextPrimary if not explicitly set)
                var fillBinding = new Binding(nameof(Foreground))
                {
                    Source = this,
                    FallbackValue = Brushes.White
                };
                path.SetBinding(Shape.FillProperty, fillBinding);

                Content = path;
                return;
            }

            // 3. Fall back to Text Glyph
            if (!string.IsNullOrEmpty(FallbackGlyph))
            {
                var text = new TextBlock
                {
                    Text = FallbackGlyph,
                    FontSize = Math.Max(size - 2, 10),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                var fgBinding = new Binding(nameof(Foreground))
                {
                    Source = this,
                    FallbackValue = Brushes.White
                };
                text.SetBinding(TextBlock.ForegroundProperty, fgBinding);

                Content = text;
                return;
            }

            Content = null;
        }
    }
}
