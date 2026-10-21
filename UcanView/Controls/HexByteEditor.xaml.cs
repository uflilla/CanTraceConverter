using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UcanView.Controls
{
    public partial class HexByteEditor : UserControl
    {
        public static readonly DependencyProperty MaxBytesProperty =
            DependencyProperty.Register(
                nameof(MaxBytes),
                typeof(int),
                typeof(HexByteEditor),
                new FrameworkPropertyMetadata(8, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnMaxBytesChanged));

        private bool _internalChange;

        public HexByteEditor()
        {
            InitializeComponent();
        }

        public int MaxBytes
        {
            get => (int)GetValue(MaxBytesProperty);
            set => SetValue(MaxBytesProperty, value);
        }

        public byte[] GetBytes() => HexByteFormatter.ParseOrDefault(PART_Editor.Text, MaxBytes);

        public bool TryGetBytes(int expectedDlc, out byte[] data, out string error) =>
            HexByteFormatter.TryParseBytes(PART_Editor.Text, expectedDlc, out data, out error);

        public void SetBytes(byte[] bytes, int length)
        {
            if (bytes == null || length <= 0)
            {
                ApplyFormattedText(string.Empty);
                return;
            }

            length = Math.Min(length, Math.Min(MaxBytes, bytes.Length));
            var formatted = string.Join(" ",
                bytes.Take(length).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
            ApplyFormattedText(formatted);
        }

        private static void OnMaxBytesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is HexByteEditor editor)
                editor.EnforceMaxBytes();
        }

        private void EnforceMaxBytes()
        {
            string digits = HexByteFormatter.ExtractHexDigits(PART_Editor.Text);
            ApplyFormattedText(HexByteFormatter.FormatDigits(digits, MaxBytes));
        }

        private void Editor_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (!IsInputAllowed(e.Text))
                e.Handled = true;
        }

        private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space)
            {
                e.Handled = true;
                return;
            }

            if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                return;

            if (e.Key == Key.X && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                return;

            if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                return;

            if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                return;
        }

        private void Editor_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.DataObject.GetDataPresent(typeof(string)))
            {
                e.CancelCommand();
                return;
            }

            string paste = e.DataObject.GetData(typeof(string)) as string ?? string.Empty;
            string digits = HexByteFormatter.ExtractHexDigits(paste);
            if (digits.Length == 0)
            {
                e.CancelCommand();
                return;
            }

            e.CancelCommand();
            InsertDigitsAtCaret(digits);
        }

        private void Editor_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_internalChange)
                return;

            int caret = PART_Editor.CaretIndex;
            string before = PART_Editor.Text ?? string.Empty;
            string digits = HexByteFormatter.ExtractHexDigits(before);
            string formatted = HexByteFormatter.FormatDigits(digits, MaxBytes);

            if (before == formatted)
                return;

            int digitsBeforeCaret = HexByteFormatter.ExtractHexDigits(before.Substring(0, Math.Min(caret, before.Length))).Length;
            ApplyFormattedText(formatted);
            PART_Editor.CaretIndex = MapDigitIndexToCaret(formatted, digitsBeforeCaret);
        }

        private void Editor_LostFocus(object sender, RoutedEventArgs e)
        {
            string padded = HexByteFormatter.PadIncompleteNibbles(PART_Editor.Text ?? string.Empty);
            ApplyFormattedText(HexByteFormatter.FormatDigits(HexByteFormatter.ExtractHexDigits(padded), MaxBytes));
        }

        private bool IsInputAllowed(string input)
        {
            if (string.IsNullOrEmpty(input))
                return false;

            foreach (char c in input)
            {
                if (!HexByteFormatter.IsHexChar(c))
                    return false;
            }

            string current = HexByteFormatter.ExtractHexDigits(PART_Editor.Text ?? string.Empty);
            int selectionLength = PART_Editor.SelectionLength;
            string selectedDigits = HexByteFormatter.ExtractHexDigits(
                PART_Editor.SelectedText ?? string.Empty);

            int effectiveLength = current.Length - selectedDigits.Length;
            string incoming = HexByteFormatter.ExtractHexDigits(input);
            return effectiveLength + incoming.Length <= MaxBytes * 2;
        }

        private void InsertDigitsAtCaret(string digits)
        {
            int caret = PART_Editor.CaretIndex;
            int selLength = PART_Editor.SelectionLength;
            string text = PART_Editor.Text ?? string.Empty;

            string left = caret > 0 ? text.Substring(0, caret) : string.Empty;
            string right = caret + selLength <= text.Length ? text.Substring(caret + selLength) : string.Empty;

            string mergedDigits = HexByteFormatter.ExtractHexDigits(left + digits + right);
            string formatted = HexByteFormatter.FormatDigits(mergedDigits, MaxBytes);
            int newDigitIndex = HexByteFormatter.ExtractHexDigits(left).Length + digits.Length;
            ApplyFormattedText(formatted);
            PART_Editor.CaretIndex = MapDigitIndexToCaret(formatted, newDigitIndex);
        }

        private void ApplyFormattedText(string formatted)
        {
            _internalChange = true;
            PART_Editor.Text = formatted;
            _internalChange = false;
        }

        private static int MapDigitIndexToCaret(string formatted, int digitIndex)
        {
            if (string.IsNullOrEmpty(formatted))
                return 0;

            if (digitIndex <= 0)
                return 0;

            int digitsSeen = 0;
            for (int i = 0; i < formatted.Length; i++)
            {
                if (HexByteFormatter.IsHexChar(formatted[i]))
                {
                    digitsSeen++;
                    if (digitsSeen >= digitIndex)
                        return i + 1;
                }
            }

            return formatted.Length;
        }
    }
}
