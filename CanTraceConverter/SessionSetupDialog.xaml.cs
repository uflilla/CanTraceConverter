using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CanTraceConverter.Models;
using CanTraceConverter.Services;

namespace CanTraceConverter
{
    public partial class SessionSetupDialog : Window
    {
        public SessionConfig SessionConfig { get; private set; }
        private int _autoRetryCount;

        public SessionSetupDialog()
        {
            InitializeComponent();
            txtLogDir.Text = SessionConfig.DefaultLogDirectory;
            txtTitle.TextChanged += (_, __) => UpdatePreview();
            txtLogDir.TextChanged += (_, __) => UpdatePreview();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            RefreshDevices(scheduleRetry: true);
            UpdatePreview();
        }

        private void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            RefreshDevices(scheduleRetry: false);
        }

        private void RefreshDevices(bool scheduleRetry)
        {
            DeviceEnumerationResult scan;
            try
            {
                scan = CanBusService.EnumerateDevices();
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex, "Device enumeration failed.");
                scan = new DeviceEnumerationResult
                {
                    DriverAvailable = false,
                    Warning = $"Unable to scan for devices: {ex.Message}",
                    Devices = new[]
                    {
                        new CanDeviceItem
                        {
                            DeviceNumber = 255,
                            SerialNumber = 0,
                            Label = "First available device (auto-detect)"
                        }
                    }
                };
            }

            var devices = new List<CanDeviceItem>(scan.Devices ?? Array.Empty<CanDeviceItem>());
            bool onlyFallback = devices.Count == 0
                || (devices.Count == 1 && devices[0].DeviceNumber == 255 && string.IsNullOrEmpty(devices[0].Label) == false
                    && devices[0].Label.IndexOf("auto-detect", StringComparison.OrdinalIgnoreCase) >= 0);

            if (devices.Count == 0)
            {
                devices.Add(new CanDeviceItem
                {
                    DeviceNumber = 255,
                    SerialNumber = 0,
                    Label = "First available device (auto-detect)"
                });
                onlyFallback = true;
            }

            byte? previousDevice = (cmbDevice.SelectedItem as CanDeviceItem)?.DeviceNumber;
            cmbDevice.ItemsSource = null;
            cmbDevice.ItemsSource = devices;

            int selectedIndex = 0;
            if (previousDevice.HasValue)
            {
                int idx = devices.FindIndex(d => d.DeviceNumber == previousDevice.Value);
                if (idx >= 0)
                    selectedIndex = idx;
            }
            cmbDevice.SelectedIndex = selectedIndex;

            if (!string.IsNullOrWhiteSpace(scan.Warning))
            {
                AppLog.Warn("Device scan warning: {0}", scan.Warning);
                txtDeviceWarning.Text = scan.Warning;
                borderDeviceWarning.Visibility = Visibility.Visible;
            }
            else if (onlyFallback)
            {
                txtDeviceWarning.Text = "No USB-CAN devices detected yet. Click Refresh after connecting hardware.";
                borderDeviceWarning.Visibility = Visibility.Visible;
            }
            else
            {
                borderDeviceWarning.Visibility = Visibility.Collapsed;
            }

            // Cold-start USB/driver readiness: retry a couple of times automatically.
            if (scheduleRetry && onlyFallback && _autoRetryCount < 3)
            {
                _autoRetryCount++;
                var delayMs = 250 * _autoRetryCount;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
                timer.Tick += (s, __) =>
                {
                    timer.Stop();
                    if (IsLoaded)
                        RefreshDevices(scheduleRetry: true);
                };
                timer.Start();
            }
        }

        private void UpdatePreview()
        {
            try
            {
                var cfg = BuildConfig(false);
                if (cfg != null)
                    txtDbPreview.Text = "Database: " + cfg.BuildDatabasePath();
            }
            catch
            {
                txtDbPreview.Text = "Database: (invalid path)";
            }
        }

        private SessionConfig BuildConfig(bool validate)
        {
            if (validate && string.IsNullOrWhiteSpace(txtTitle.Text))
            {
                MessageBox.Show("Please enter a session title.", "Required",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            var device = cmbDevice.SelectedItem as CanDeviceItem;
            if (validate && device == null)
            {
                MessageBox.Show("Please select a device.", "Required",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            var channelItem = cmbChannel.SelectedItem as ComboBoxItem;
            var baudItem = cmbBaud.SelectedItem as ComboBoxItem;

            byte channel = 0;
            if (channelItem?.Tag != null)
                byte.TryParse(channelItem.Tag.ToString(), out channel);

            int baud = 500;
            if (baudItem?.Tag != null)
                int.TryParse(baudItem.Tag.ToString(), out baud);

            return new SessionConfig
            {
                Title = txtTitle.Text?.Trim() ?? "Session",
                DeviceNumber = device?.DeviceNumber ?? 255,
                Channel = channel,
                BaudRateKbps = baud,
                ListenOnly = chkListenOnly.IsChecked == true,
                LogDirectory = txtLogDir.Text?.Trim() ?? SessionConfig.DefaultLogDirectory
            };
        }

        private void BtnBrowseLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = "Select log directory",
                    SelectedPath = Directory.Exists(txtLogDir.Text) ? txtLogDir.Text : SessionConfig.DefaultLogDirectory
                };
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    txtLogDir.Text = dlg.SelectedPath;
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex, "Could not browse for log folder.");
                MessageBox.Show($"Could not browse for folder:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            SessionConfig = BuildConfig(true);
            if (SessionConfig == null)
                return;

            try
            {
                Directory.CreateDirectory(SessionConfig.LogDirectory);
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Cannot create session log directory '{0}'.", SessionConfig.LogDirectory);
                MessageBox.Show($"Cannot create log directory:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            DialogResult = true;
            AppLog.Info("Session setup accepted: title='{0}', device={1}, channel={2}, baud={3}, listenOnly={4}.",
                SessionConfig.Title, SessionConfig.DeviceNumber, SessionConfig.Channel,
                SessionConfig.BaudRateKbps, SessionConfig.ListenOnly);
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
