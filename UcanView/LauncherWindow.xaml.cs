using System;
using System.Windows;
using UcanView.Services;
using Microsoft.Win32;

namespace UcanView
{
    public partial class LauncherWindow : Window
    {
        public LauncherWindow()
        {
            InitializeComponent();
            UpdateThemeButton();
        }

        private void BtnTheme_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.ToggleTheme();
            UpdateThemeButton();
        }

        private void UpdateThemeButton()
        {
            btnTheme.Content = ThemeManager.IsDarkTheme
                ? "Switch to Light Theme"
                : "Switch to Dark Theme";
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
                Filter = "Session database or CAN trace|*.db;*.txt;*.ucantrc|Session database|*.db|CAN trace text|*.txt|Binary trace|*.ucantrc|All files|*.*",
                Title = "Open trace for analysis"
            };
            if (dlg.ShowDialog() != true)
                return;

            var kind = dlg.FileName.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
                ? AnalysisSource.Database
                : dlg.FileName.EndsWith(".ucantrc", StringComparison.OrdinalIgnoreCase)
                    ? AnalysisSource.BinaryTrace
                    : AnalysisSource.TextFile;

            AppLog.Info("Opening analysis for '{0}' ({1}).", dlg.FileName, kind);
            AnalysisWindow.Open(this, dlg.FileName, kind);
        }

        private void BtnSdoPackets_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Session database or CAN trace|*.db;*.txt;*.ucantrc|Session database|*.db|Converted trace text|*.txt|Binary trace|*.ucantrc|All files|*.*",
                Title = "Open a trace — or select two (both channels of one adapter) to merge them",
                Multiselect = true
            };
            if (dlg.ShowDialog() != true)
                return;

            var files = dlg.FileNames;
            if (files.Length > 2)
            {
                MessageBox.Show("Select one trace, or two traces to merge.", "SDO Packet Trace",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (files.Length == 2)
            {
                AppLog.Info("Opening merged SDO packet trace for '{0}' and '{1}'.", files[0], files[1]);
                SdoPacketWindow.OpenMerged(null, files[0], files[1]);
                return;
            }

            AppLog.Info("Opening SDO packet trace for '{0}'.", dlg.FileName);
            SdoPacketWindow.OpenFile(null, dlg.FileName);
        }
    }
}
