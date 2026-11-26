namespace RevitAgent.Host.Controls;

using System.Windows;
using System.Windows.Controls;

/// <summary>
/// Attached property to enable MVVM data binding on a WPF <see cref="PasswordBox"/>.
/// </summary>
public static class PasswordBoxHelper
{
    public static readonly DependencyProperty BoundPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BoundPassword",
            typeof(string),
            typeof(PasswordBoxHelper),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    public static readonly DependencyProperty BindPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BindPassword",
            typeof(bool),
            typeof(PasswordBoxHelper),
            new PropertyMetadata(false, OnBindPasswordChanged));

    private static readonly DependencyProperty UpdatingPasswordProperty =
        DependencyProperty.RegisterAttached(
            "UpdatingPassword",
            typeof(bool),
            typeof(PasswordBoxHelper),
            new PropertyMetadata(false));

    public static void SetBindPassword(DependencyObject dp, bool value) =>
        dp.SetValue(BindPasswordProperty, value);

    public static bool GetBindPassword(DependencyObject dp) =>
        (bool)dp.GetValue(BindPasswordProperty);

    public static string GetBoundPassword(DependencyObject d) =>
        (string)d.GetValue(BoundPasswordProperty);

    public static void SetBoundPassword(DependencyObject d, string value) =>
        d.SetValue(BoundPasswordProperty, value);

    private static bool GetUpdatingPassword(DependencyObject dp) =>
        (bool)dp.GetValue(UpdatingPasswordProperty);

    private static void SetUpdatingPassword(DependencyObject dp, bool value) =>
        dp.SetValue(UpdatingPasswordProperty, value);

    private static void OnBindPasswordChanged(DependencyObject dp, DependencyPropertyChangedEventArgs e)
    {
        if (dp is not PasswordBox box) return;

        bool wasBound = (bool)e.OldValue;
        bool isBound = (bool)e.NewValue;

        if (wasBound)
        {
            box.PasswordChanged -= HandlePasswordChanged;
        }

        if (isBound)
        {
            box.PasswordChanged += HandlePasswordChanged;
        }
    }

    private static void HandlePasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox box) return;

        SetUpdatingPassword(box, true);
        SetBoundPassword(box, box.Password);
        SetUpdatingPassword(box, false);
    }

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;

        if (GetUpdatingPassword(box)) return;

        box.Password = (string)e.NewValue ?? string.Empty;
    }
}
