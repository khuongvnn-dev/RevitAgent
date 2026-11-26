using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace RevitAgent.UiTestApp;

public sealed record Order(int Id, string Customer, string City, decimal Amount);

/// <summary>Button content object (ToString = full type name), like Autodesk.Windows ribbon items.</summary>
public sealed class RibbonItemVm
{
    public RibbonItemVm(string text) => Text = text;

    public string Text { get; }
}

public partial class MainWindow : Window
{
    private int _clicks;

    public MainWindow()
    {
        InitializeComponent();
        gridOrders.ItemsSource = new[]
        {
            new Order(1, "Alice", "Hà Nội", 120.5m),
            new Order(2, "Bình", "Đà Nẵng", 99m),
            new Order(3, "Chi", "Phú Quốc", 15.25m),
            new Order(4, "Dũng", "Huế", 300m),
            new Order(5, "Eve", "Hà Nội", 42m),
        };
        btnRibbonLike.Content = new RibbonItemVm("Ribbon Label");
        lstLong.ItemsSource = Enumerable.Range(1, 100).Select(i => "Item " + i.ToString("000", CultureInfo.InvariantCulture)).ToArray();
    }

    private void OnSubmit(object sender, RoutedEventArgs e)
    {
        var city = (cmbCity.SelectedItem as ComboBoxItem)?.Content as string ?? "-";
        lblStatus.Text = $"Saved: {txtName.Text} / {city}";
    }

    private void OnCount(object sender, RoutedEventArgs e)
    {
        _clicks++;
        lblCount.Text = "Clicks: " + _clicks.ToString(CultureInfo.InvariantCulture);
    }

    private void OnOpenChild(object sender, RoutedEventArgs e)
    {
        var child = new ChildWindow { Owner = this };
        if (child.ShowDialog() == true)
        {
            lblStatus.Text = "Child: " + child.Value;
        }
        else
        {
            lblStatus.Text = "Child cancelled";
        }
    }

    private void OnMessage(object sender, RoutedEventArgs e)
    {
        // WPF MessageBox is the Win32 MessageBox (#32770) owned by this window.
        var r = MessageBox.Show(this, "Proceed with the test step?", "Test Message", MessageBoxButton.YesNo, MessageBoxImage.Question);
        lblStatus.Text = "Message: " + r;
    }

    private void OnDelayed(object sender, RoutedEventArgs e)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            btnLate.Visibility = Visibility.Visible;
        };
        timer.Start();
    }

    private void OnLate(object sender, RoutedEventArgs e) => lblStatus.Text = "Late clicked";

    private void OnCanvasDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(canvasArea);
        lblCanvas.Text = string.Create(CultureInfo.InvariantCulture, $"Canvas click at {(int)p.X},{(int)p.Y}");
    }
}
