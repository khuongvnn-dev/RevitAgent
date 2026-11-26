namespace RevitAgent.Host.Views
{
    using System.ComponentModel;
    using System.Windows;
    using System.Windows.Controls;
    using Microsoft.Extensions.DependencyInjection;
    using RevitAgent.Host.Services;
    using RevitAgent.Host.ViewModels;

    /// <summary>Code-behind only wires the view model (resolved from DI) and the remove confirmation.</summary>
    public partial class AddinAllowlistView : UserControl
    {
        public AddinAllowlistView()
        {
            InitializeComponent();
            if (DesignerProperties.GetIsInDesignMode(this) || App.Services is null)
            {
                return;
            }

            var vm = App.Services.GetRequiredService<AddinAllowlistViewModel>();
            var localization = App.Services.GetService<ILocalizationService>();
            vm.ConfirmRemove = name => MessageBox.Show(
                Window.GetWindow(this),
                string.Format(localization?.GetString("Str_AddinConfirmRemove") ?? "Remove '{0}'?", name),
                localization?.GetString("Str_AddinTitle") ?? "Add-in",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;
            DataContext = vm;
        }
    }
}
