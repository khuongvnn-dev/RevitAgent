using System.Globalization;
using System.Windows;
using System.Windows.Threading;

namespace RevitAgent.UiTestApp;

/// <summary>
/// Test target for the gateway's UI automation tools. Arguments:
/// <c>--lifetime &lt;seconds&gt;</c> exits on its own (default 600) so a crashed test run never leaves it behind;
/// <c>--title &lt;text&gt;</c> overrides the main window title.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var lifetime = 600;
        string? title = null;
        for (int i = 0; i < e.Args.Length - 1; i++)
        {
            if (e.Args[i] == "--lifetime" && int.TryParse(e.Args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
            {
                lifetime = s;
            }
            else if (e.Args[i] == "--title")
            {
                title = e.Args[i + 1];
            }
        }

        var main = new MainWindow();
        if (title is not null)
        {
            main.Title = title;
        }

        MainWindow = main;
        main.Show();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(5, lifetime)) };
        timer.Tick += (_, _) => Environment.Exit(0);
        timer.Start();
    }
}
