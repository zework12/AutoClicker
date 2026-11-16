using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using AutoClicker.Core;
using AutoClicker.Interop;

namespace AutoClicker.Views;

/// <summary>
/// A dim window of our own across every monitor that takes exactly one click
/// and reports where it landed. The click is an ordinary WPF mouse event on
/// this window; nothing is hooked.
/// </summary>
public partial class PointPickerWindow : Window
{
    private bool _done;

    public PointPickerWindow()
    {
        InitializeComponent();
    }

    /// <summary>The picked point in physical screen pixels; valid when ShowDialog returned true.</summary>
    public int PickedX { get; private set; }
    public int PickedY { get; private set; }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Sized in physical pixels directly: WPF units would be wrong as soon as
        // monitors with different scaling are involved.
        UiNative.SetWindowPos(
            new WindowInteropHelper(this).Handle,
            UiNative.HWND_TOPMOST,
            Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN),
            Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
            Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN),
            Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN),
            0);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Activate();
        Focus();
        MoveTip(Mouse.GetPosition(Root));
    }

    private void OnMouseMove(object sender, MouseEventArgs e) => MoveTip(e.GetPosition(Root));

    private void MoveTip(Point at)
    {
        if (Native.GetCursorPos(out var p)) Coordinates.Text = $"X {p.X}   Y {p.Y}";

        Tip.Visibility = Visibility.Visible;
        Tip.UpdateLayout();

        // Sit below-right of the cursor, flipping near the far edges.
        double x = at.X + 18, y = at.Y + 20;
        if (x + Tip.ActualWidth > Root.ActualWidth) x = at.X - 14 - Tip.ActualWidth;
        if (y + Tip.ActualHeight > Root.ActualHeight) y = at.Y - 14 - Tip.ActualHeight;
        Canvas.SetLeft(Tip, Math.Max(0, x));
        Canvas.SetTop(Tip, Math.Max(0, y));
    }

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!Native.GetCursorPos(out var p))
        {
            Finish(false);
            return;
        }

        PickedX = p.X;
        PickedY = p.Y;
        Finish(true);
    }

    private void OnRightDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Finish(false);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Finish(false);
    }

    // Alt-Tab away: a topmost dim sheet must not be left hanging over the desktop.
    private void OnDeactivated(object? sender, EventArgs e) => Finish(false);

    private void Finish(bool picked)
    {
        if (_done) return;
        _done = true;
        DialogResult = picked;
    }
}
