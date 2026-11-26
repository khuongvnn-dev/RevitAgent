namespace RevitAgent.Host
{
    using System;
    using System.ComponentModel;
    using System.Windows;
    using System.Windows.Threading;
    using RevitAgent.Host.ViewModels;

    /// <summary>
    /// Pure View code-behind for MainWindow.xaml.
    /// All business logic, automation, and IPC orchestration is delegated to MainViewModel.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly DispatcherTimer _timer;

        public MainWindow(MainViewModel viewModel)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            DataContext = _viewModel;

            InitializeComponent();

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += (s, e) => _viewModel.OnTick();

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _timer.Start();
            await _viewModel.InitializeAsync();
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            _timer.Stop();
        }
    }
}
