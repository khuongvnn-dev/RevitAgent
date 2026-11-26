using System.Windows;

namespace RevitAgent.UiTestApp;

/// <summary>Modal child window (ShowDialog, owned by the main window) — like an add-in dialog inside Revit.</summary>
public partial class ChildWindow : Window
{
    public ChildWindow() => InitializeComponent();

    public string Value => txtChild.Text;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
