using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AutoClicker.Core;
using AutoClicker.Interop;

namespace AutoClicker.Views;

/// <summary>
/// The status pill. An ordinary topmost window of our own: click-through, never
/// activated, never part of the game's rendering.
/// </summary>
public partial class OverlayWindow : Window
{
    private const double EdgeMargin = 12;   // DIPs between the pill and the screen edge

    private readonly AppSettings _settings;
    private readonly StatusSources _status;
    private readonly DispatcherTimer _timer;
    private IntPtr _handle;
    private int _ticks;

    internal OverlayWindow(AppSettings settings, StatusSources status)
    {
        InitializeComponent();
        _settings = settings;
        _status = status;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += OnTick;
        Refresh();
    }

    /// <summary>The pill itself, detached from the window, for off-screen rendering.</summary>
    internal FrameworkElement DetachContent()
    {
        var root = Root;
        Content = null;
        return root;
    }

    /// <summary>Applies Overlay.Visible / Corner / Opacity after the user changed them.</summary>
    public void ApplySettings()
    {
        Opacity = Math.Clamp(_settings.Overlay.Opacity, 0.2, 1.0);

        if (_settings.Overlay.Visible)
        {
            if (!IsVisible) Show();
            _timer.Start();
            Refresh();
            Reposition();
        }
        else
        {
            _timer.Stop();
            if (IsVisible) Hide();
        }
    }

    /// <summary>Redraws the rows from the current runner state.</summary>
    public void Refresh()
    {
        var idle = (Brush)FindResource("TextFaint");
        var running = (Brush)FindResource("Success");
        var paused = (Brush)FindResource("Warning");
        var muted = (Brush)FindResource("TextMuted");
        var primary = (Brush)FindResource("TextPrimary");

        var mouse = _status.Mouse();
        MouseName.Text = _settings.Mouse.Button switch
        {
            MouseButtonKind.Left => "ЛКМ",
            MouseButtonKind.Right => "ПКМ",
            _ => "СКМ",
        };
        MouseDot.Fill = mouse.Color(idle, running, paused);
        MouseState.Text = mouse.Describe("к/с", "выкл", "зажата");
        MouseState.Foreground = mouse.Running ? (mouse.Paused ? paused : primary) : muted;

        var key = _status.Keyboard();
        KeyName.Text = KeyNames.Combo(_settings.Keyboard);
        KeyDot.Fill = key.Color(idle, running, paused);
        KeyState.Text = key.Describe("н/с", "выкл", "зажата");
        KeyState.Foreground = key.Running ? (key.Paused ? paused : primary) : muted;

        var camera = _status.Camera();
        CamDot.Fill = camera.Color(idle, running, paused);
        CamState.Text = camera.Describe("", "выкл");
        CamState.Foreground = camera.Running ? (camera.Paused ? paused : primary) : muted;

        var random = _status.RandomMove();
        RndDot.Fill = random.Color(idle, running, paused);
        RndState.Text = random.Describe("", "выкл");
        RndState.Foreground = random.Running ? (random.Paused ? paused : primary) : muted;

        MenuHint.Text = _settings.MenuHotkey.VirtualKey == 0
            ? ""
            : $"{KeyNames.Combo(_settings.MenuHotkey)} — меню";
        MenuHint.Visibility = MenuHint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        Native.MakeClickThrough(_handle);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Reposition();

    private void OnTick(object? sender, EventArgs e)
    {
        Refresh();
        Reposition();

        // Another topmost window (or a borderless game) can end up above us;
        // climb back every couple of seconds without taking the focus.
        if (++_ticks % 14 == 0 && _handle != IntPtr.Zero)
        {
            UiNative.SetWindowPos(_handle, UiNative.HWND_TOPMOST, 0, 0, 0, 0,
                UiNative.SWP_NOMOVE | UiNative.SWP_NOSIZE | UiNative.SWP_NOACTIVATE);
        }
    }

    /// <summary>
    /// Puts the pill into the chosen corner of the monitor that shows the
    /// foreground window (the primary one if there is none). Works in physical
    /// pixels so mixed-DPI setups come out right.
    /// </summary>
    private void Reposition()
    {
        if (_handle == IntPtr.Zero || !IsVisible || ActualWidth <= 0) return;

        var area = UiNative.WorkAreaOf(Native.GetForegroundWindow());
        if (area.Right <= area.Left) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        int margin = (int)Math.Round(EdgeMargin * dpi.DpiScaleX);

        var corner = _settings.Overlay.Corner;
        bool right = corner is OverlayCorner.TopRight or OverlayCorner.BottomRight;
        bool bottom = corner is OverlayCorner.BottomLeft or OverlayCorner.BottomRight;
        int x = right ? area.Right - margin - width : area.Left + margin;
        int y = bottom ? area.Bottom - margin - height : area.Top + margin;

        if (UiNative.GetWindowRect(_handle, out var now) && now.Left == x && now.Top == y) return;

        UiNative.SetWindowPos(_handle, UiNative.HWND_TOPMOST, x, y, 0, 0,
            UiNative.SWP_NOSIZE | UiNative.SWP_NOACTIVATE);
    }
}
