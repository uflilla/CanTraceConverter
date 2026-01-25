using CanTraceConverter.ViewModels;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.IO;

namespace CanTraceConverter
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private TraceConverter _converter;
        private bool _isConverting;

        public MainWindow()
        {
            InitializeComponent();
            _converter = new TraceConverter(Dispatcher);

            _converter.StatusUpdated += status => txtStatus.Text = status;
            _converter.ProgressUpdated += count => Title = $"USB-CAN Trace Converter – {count:N0} messages";
        }

        private async void btnConvert_Click(object sender, RoutedEventArgs e)
        {
            if (_isConverting) return;
            if (string.IsNullOrWhiteSpace(txtInputFile.Text))
            {
                MessageBox.Show("Please select an input file.", "Missing input",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _isConverting = true;
            btnConvert.IsEnabled = false;
            btnConvert.Content = "Converting...";
            txtOutput.Clear();
            txtStatus.Text = "Starting conversion...";

            try
            {
                bool diff = chkDiffTime.IsChecked == true;
                bool writeToFile = !string.IsNullOrWhiteSpace(txtOutputFile.Text);
                var inputPath = txtInputFile.Text;
                var outputPath = txtOutputFile.Text;

                // Run conversion in background
                var (result, messageCount, errorCode, header) = await _converter.ConvertAsync(
                    inputPath,
                    diff
                );

                if (errorCode == 0)
                {
                    // Display output in UI
                    txtOutput.Text = result;

                    // Write to file if output path is specified
                    if (writeToFile)
                    {
                        try
                        {
                            await Task.Run(() => File.WriteAllText(outputPath, result, System.Text.Encoding.UTF8));
                            txtStatus.Text = $"Conversion successful: {messageCount:N0} messages. File saved to: {outputPath}";
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Failed to write output file:\n{ex.Message}",
                                "File Write Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            txtStatus.Text = $"Conversion successful but file write failed: {ex.Message}";
                        }
                    }
                    else
                    {
                        txtStatus.Text = $"Conversion successful: {messageCount:N0} messages processed.";
                    }

                    // Scroll to top
                    txtOutput.ScrollToHome();
                }
                else
                {
                    txtStatus.Text = $"Conversion failed with error code 0x{errorCode:X4}";
                    MessageBox.Show($"Conversion failed with error code 0x{errorCode:X4}",
                        "Conversion Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"Error: {ex.Message}";
                MessageBox.Show($"An unexpected error occurred:\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isConverting = false;
                btnConvert.IsEnabled = true;
                btnConvert.Content = "Convert";
            }
        }

        private void btnBrowseInput_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Binary CAN trace|*.ucantrc|All files|*.*",
                Title = "Select input binary trace file"
            };
            if (dlg.ShowDialog() == true)
            {
                txtInputFile.Text = dlg.FileName;

                // Auto-generate output file path with .txt extension
                string directory = Path.GetDirectoryName(dlg.FileName);
                string baseName = Path.GetFileNameWithoutExtension(dlg.FileName);
                txtOutputFile.Text = Path.Combine(directory, baseName + ".txt");

                txtStatus.Text = "Input file selected.";
            }
        }

        private void btnBrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Text file|*.txt|All files|*.*",
                Title = "Select output text file",
                DefaultExt = "txt",
                AddExtension = true
            };
            if (!string.IsNullOrWhiteSpace(txtInputFile.Text))
            {
                string baseName = Path.GetFileNameWithoutExtension(txtInputFile.Text);
                dlg.FileName = baseName + ".txt";
            }
            if (dlg.ShowDialog() == true)
            {
                txtOutputFile.Text = dlg.FileName;
                txtStatus.Text = "Output file selected.";
            }
        }

        private void btnClear_Click(object sender, RoutedEventArgs e)
        {
            txtOutput.Clear();
            txtStatus.Text = "Output cleared.";
        }

        private void btnClearFiles_Click(object sender, RoutedEventArgs e)
        {
            txtInputFile.Clear();
            txtOutputFile.Clear();
            txtStatus.Text = "File paths cleared.";
        }
    }
}