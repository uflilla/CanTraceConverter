using System;
using System.Windows;
using Microsoft.Win32;

namespace CanTraceConverter
{
    public partial class LauncherWindow : Window
    {
        public LauncherWindow()
        {
            InitializeComponent();
        }

        private void BtnNewMonitor_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new SessionSetupDialog { Owner = this };
                if (dialog.ShowDialog() != true || dialog.SessionConfig == null)
                    return;

                var monitor = new MonitorWindow(dialog.SessionConfig);
                monitor.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not start monitor session:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnConverter_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var converter = new MainWindow();
                converter.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open converter:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnAnalyzeDb_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Session database|*.db|All files|*.*",
                Title = "Open session database"
            };
            if (dlg.ShowDialog() != true)
                return;

            try
            {
                var analysis = new AnalysisWindow(dlg.FileName, AnalysisSource.Database);
                analysis.Owner = this;
                analysis.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open database:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
