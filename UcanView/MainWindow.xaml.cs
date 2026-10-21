using UcanView.ViewModels;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Path = System.IO.Path;

namespace UcanView
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>Most lines copied to the clipboard at once; the full text is in the output file.</summary>
        private const int MaxCopyLines = 100000;

        private TraceConverter _converter;
        private bool _isConverting;

        /// <summary>The converted trace shown in the output list; holds the trace file open.</summary>
        private TraceLineList _output;

        public MainWindow()
        {
            InitializeComponent();
            _converter = new TraceConverter(Dispatcher);

            _converter.StatusUpdated += status => txtStatus.Text = status;
            _converter.ProgressUpdated += (count, total) =>
            {
                Title = $"UCAN View - Trace Converter – {count:N0} messages";
                if (_isConverting && total > 0)
                    txtStatus.Text = $"Writing text file... {count:N0} of {total:N0} messages ({count * 100 / total}%)";
            };

            CommandBindings.Add(new System.Windows.Input.CommandBinding(
                System.Windows.Input.ApplicationCommands.Copy, CopyOutput_Executed, CopyOutput_CanExecute));
            Closed += (_, __) => SetOutput(null);
        }

        private void SetOutput(TraceLineList output)
        {
            lstOutput.ItemsSource = output;
            _output?.Dispose();
            _output = output;
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
            if (!File.Exists(txtInputFile.Text))
            {
                MessageBox.Show("Input file does not exists.", "Invalid input",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!string.IsNullOrWhiteSpace(txtOutputFile.Text) && !Directory.Exists(txtOutputFile.Text))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(txtOutputFile.Text));
                TxtOutputFile_OnTextChanged(null,null);
            }

            _isConverting = true;
            btnConvert.IsEnabled = false;
            btnConvert.Content = "Converting...";
            SetOutput(null);
            txtStatus.Text = "Starting conversion...";

            try
            {
                bool diff = chkDiffTime.IsChecked == true;
                bool writeToFile = !string.IsNullOrWhiteSpace(txtOutputFile.Text);
                var inputPath = txtInputFile.Text;
                var outputPath = writeToFile ? txtOutputFile.Text : null;

                // Runs on a background thread and streams the text file; the await resumes
                // back on the UI thread.
                var result = await _converter.ConvertAsync(inputPath, diff, outputPath);

                if (result.ErrorCode == 0)
                {
                    // Lines are formatted from the trace only as they scroll into view.
                    SetOutput(new TraceLineList(inputPath, result.HeaderText, diff));
                    if (lstOutput.Items.Count > 0)
                        lstOutput.ScrollIntoView(lstOutput.Items[0]);

                    txtStatus.Text = writeToFile
                        ? $"Conversion successful: {result.MessageCount:N0} messages. File saved to: {outputPath}"
                        : $"Conversion successful: {result.MessageCount:N0} messages processed.";
                    Title = $"UCAN View - Trace Converter – {result.MessageCount:N0} messages";
                }
                else if (result.ErrorCode == 0x7FFF && writeToFile)
                {
                    txtStatus.Text = "Conversion failed while writing the output file.";
                    MessageBox.Show($"Failed to write output file:\n{outputPath}",
                        "File Write Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    txtStatus.Text = $"Conversion failed with error code 0x{result.ErrorCode:X4}";
                    MessageBox.Show($"Conversion failed with error code 0x{result.ErrorCode:X4}",
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
            SetOutput(null);
            txtStatus.Text = "Output cleared.";
            Title = $"UCAN View - Trace Converter";
        }

        private void btnClearFiles_Click(object sender, RoutedEventArgs e)
        {
            txtInputFile.Clear();
            txtOutputFile.Clear();
            txtStatus.Text = "File paths cleared.";
        }

        private void btnOpenOutputDir_Click(object sender, RoutedEventArgs e)
        {
            if (!IsValidPath(txtOutputFile.Text))
            {
                MessageBox.Show("Invalid output directory path!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            var dir = Path.GetDirectoryName(txtOutputFile.Text);
            if (Directory.Exists(dir))
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    Arguments = dir,
                    FileName = "explorer.exe"
                };
                Process.Start(startInfo);
            }
            else
            {
                MessageBox.Show("Output directory does not exists!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void TxtOutputFile_OnTextChanged(object sender, TextChangedEventArgs e)
        {
            btnOpenOutputDir.IsEnabled = !string.IsNullOrWhiteSpace(txtOutputFile.Text) && IsValidPath(txtOutputFile.Text) &&
                                        Directory.Exists(Path.GetDirectoryName(txtOutputFile.Text));
        }

        private bool IsValidPath(string path)
        {
            try
            {
                string directory = Path.GetDirectoryName(path);
                return !string.IsNullOrWhiteSpace(directory);
            }
            catch
            {
                return false;
            }
        }

        private void btnAnalyze_Click(object sender, RoutedEventArgs e)
        {
            if (_output == null)
            {
                MessageBox.Show("No converted data available to analyze. Please convert a trace file first.",
                    "No Data", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Read from the binary trace itself: re-parsing the converted text needed the
            // whole text in memory as well as the messages.
            AnalysisWindow.Open(this, _output.TracePath, AnalysisSource.BinaryTrace);
        }

        private void LstOutput_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Select All would materialise every line of the trace.
            if (e.Key == System.Windows.Input.Key.A &&
                (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
            {
                e.Handled = true;
                txtStatus.Text = "Select All is not available for the output list — the full text is in the output file.";
            }
        }

        private void CopyOutput_CanExecute(object sender, System.Windows.Input.CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = _output != null && lstOutput.SelectedItems.Count > 0;
        }

        private void CopyOutput_Executed(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
        {
            var lines = lstOutput.SelectedItems.OfType<TraceLine>().OrderBy(l => l.Index).Take(MaxCopyLines).ToList();
            if (lines.Count == 0)
                return;

            var sb = new StringBuilder();
            foreach (var line in lines)
                sb.AppendLine(line.Text);
            Clipboard.SetText(sb.ToString());

            txtStatus.Text = lstOutput.SelectedItems.Count > MaxCopyLines
                ? $"Copied the first {MaxCopyLines:N0} selected lines."
                : $"Copied {lines.Count:N0} line(s).";
        }
    }
}