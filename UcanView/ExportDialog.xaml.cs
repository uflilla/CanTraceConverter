using System.Globalization;
using System.Windows;

namespace UcanView
{
    public partial class ExportDialog : Window
    {
        public double? TimeFromMs { get; private set; }
        public double? TimeToMs { get; private set; }

        public ExportDialog()
        {
            InitializeComponent();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            TimeFromMs = ParseOptionalDouble(txtFrom.Text);
            TimeToMs = ParseOptionalDouble(txtTo.Text);

            if (TimeFromMs.HasValue && TimeToMs.HasValue && TimeFromMs > TimeToMs)
            {
                MessageBox.Show("'From' must be less than or equal to 'To'.", "Invalid range",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
            Close();
        }

        private static double? ParseOptionalDouble(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return v;
            return null;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
