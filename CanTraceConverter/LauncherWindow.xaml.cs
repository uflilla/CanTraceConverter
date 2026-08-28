using System;
using System.Windows;
using CanTraceConverter.Services;
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

                AppLog.Info("Opening monitor session '{0}'.", dialog.SessionConfig.Title);
                var monitor = new MonitorWindow(dialog.SessionConfig);
                monitor.Show();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Could not start monitor session.");
                MessageBox.Show($"Could not start monitor session:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnConverter_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppLog.Info("Opening trace converter window.");
                var converter = new MainWindow();
                converter.Show();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Could not open converter.");
                MessageBox.Show($"Could not open converter:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnAnalyzeDb_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Session database|*.db|CAN trace text|*.txt|All files|*.*",
                Title = "Open trace for analysis"
            };
            if (dlg.ShowDialog() != true)
                return;

            var kind = dlg.FileName.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
                ? AnalysisSource.Database
                : AnalysisSource.TextFile;

            AppLog.Info("Opening analysis for '{0}' ({1}).", dlg.FileName, kind);
            AnalysisWindow.Open(this, dlg.FileName, kind);
        }
    }
}
