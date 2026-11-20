using System.Globalization;
using System.Windows.Controls;

namespace PhotoChop;

internal static class NumericWheel
{
    public static void Enable(TextBox field, double min = double.NegativeInfinity, double max = double.PositiveInfinity, Action<double>? commit = null)
    {
        field.Template = (System.Windows.Controls.ControlTemplate)field.FindResource("NumericTextBoxTemplate");
        int wheel = 0;
        string accepted = field.Text;
        bool TryValue(out double value) => double.TryParse(field.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) &&
            double.IsFinite(value) && value >= min && value <= max;

        void Commit()
        {
            if (!TryValue(out double value)) { field.Text = accepted; return; }
            accepted = field.Text;
            commit?.Invoke(value);
        }

        field.LostKeyboardFocus += (_, _) => Commit();
        field.MouseLeave += (_, _) => wheel = 0;
        field.PreviewMouseWheel += (_, e) =>
        {
            var culture = CultureInfo.CurrentCulture;
            if (field.IsReadOnly || !TryValue(out double current)) return;

            wheel += e.Delta;
            int ticks = wheel / 120;
            wheel %= 120;
            e.Handled = true;
            if (ticks == 0) return;

            string text = field.Text.Trim();
            string separator = culture.NumberFormat.NumberDecimalSeparator;
            int start = text.IndexOf(separator, StringComparison.Ordinal);
            int digits = start < 0 ? 0 : Math.Clamp(text.Length - start - separator.Length, 1, 12);
            double step = digits switch { 0 => 1, 1 => .1, _ => .01 };
            double updated = Math.Clamp(current + ticks * step, min, max);
            field.Text = updated.ToString($"F{digits}", culture);
            Commit();
            if (field.IsKeyboardFocused) field.SelectAll();
        };
    }
}
