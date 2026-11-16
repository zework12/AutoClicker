using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AutoClicker.Views;

/// <summary>
/// A number box with an optional prefix/suffix inside the field ("± 5 мс").
/// A valid number applies while typing; anything else snaps back on blur.
/// </summary>
public partial class NumberField : UserControl
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private double _value;
    private bool _settingText;

    public NumberField()
    {
        InitializeComponent();
        WriteText();
    }

    /// <summary>Raised only when the user changes the value, not when <see cref="Value"/> is assigned.</summary>
    public event EventHandler? ValueChanged;

    public double Minimum { get; set; }
    public double Maximum { get; set; } = 1_000_000;

    /// <summary>Digits allowed after the decimal separator. 0 = whole numbers.</summary>
    public int Decimals { get; set; }

    public double Value
    {
        get => _value;
        set
        {
            _value = Normalize(value);
            WriteText();
        }
    }

    public string Prefix
    {
        get => PrefixText.Text;
        set => SetAdornment(PrefixText, value);
    }

    public string Suffix
    {
        get => SuffixText.Text;
        set => SetAdornment(SuffixText, value);
    }

    private static void SetAdornment(TextBlock block, string? text)
    {
        block.Text = text ?? "";
        block.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private double Normalize(double value) =>
        Math.Round(Math.Clamp(value, Minimum, Maximum), Decimals, MidpointRounding.AwayFromZero);

    private void WriteText()
    {
        _settingText = true;
        Input.Text = _value.ToString(Decimals > 0 ? "0." + new string('#', Decimals) : "0", Ru);
        _settingText = false;
    }

    private bool TryRead(out double value)
    {
        // Accept both "2,5" and "2.5" regardless of the system locale.
        var text = Input.Text.Trim().Replace(',', '.').Replace(" ", "").Replace(" ", "");
        return double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    private void Apply(double value)
    {
        value = Normalize(value);
        if (value.Equals(_value)) return;
        _value = value;
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingText) return;

        // Out-of-range input is left alone until blur, so typing "25" into a
        // field with a minimum of 5 doesn't fight the user on the first digit.
        if (TryRead(out var value) && value >= Minimum && value <= Maximum) Apply(value);
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        foreach (char c in e.Text)
        {
            bool ok = char.IsAsciiDigit(c) || (Decimals > 0 && c is ',' or '.');
            if (!ok) { e.Handled = true; return; }
        }
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Commit();
                Input.SelectAll();
                e.Handled = true;
                break;
            case Key.Up:
                Step(+1);
                e.Handled = true;
                break;
            case Key.Down:
                Step(-1);
                e.Handled = true;
                break;
            case Key.Space:
                e.Handled = true;
                break;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!Input.IsKeyboardFocused) return;
        Step(e.Delta > 0 ? +1 : -1);
        e.Handled = true;
    }

    private void Step(int direction)
    {
        Commit();
        Apply(_value + direction);
        WriteText();
        Input.CaretIndex = Input.Text.Length;
    }

    /// <summary>Clamps whatever is typed into range, or restores the last good value.</summary>
    private void Commit()
    {
        if (TryRead(out var value)) Apply(value);
        WriteText();
    }

    private void OnChromeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Input.IsKeyboardFocused) return;
        Input.Focus();
        Input.SelectAll();
        e.Handled = true;
    }

    private void OnGotFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        Chrome.BorderBrush = (Brush)FindResource("Accent");

    private void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        Chrome.ClearValue(Border.BorderBrushProperty);
        Commit();
    }
}
