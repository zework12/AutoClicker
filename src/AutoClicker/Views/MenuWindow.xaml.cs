using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AutoClicker.Core;
using AutoClicker.Interop;

namespace AutoClicker.Views;

/// <summary>
/// The menu: a sidebar with one entry per runner plus «Настройки», and the page
/// of the selected entry on the right. Controls write straight into
/// <see cref="AppSettings"/>; saving and restarting a running runner are
/// debounced so typing a number doesn't hit the disk (or the runner) on every digit.
/// </summary>
public partial class MenuWindow : Window
{
    private readonly ClickerService? _service;   // null only in the preview renderer
    private readonly AppSettings _settings;
    private readonly StatusSources _status;
    private readonly ForegroundSwitcher _foreground = new();
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _poll;

    private IReadOnlyList<string> _failedHotkeys = [];
    private string? _saveError;
    private bool _loading = true;
    private Part _stale;
    private bool _exiting;
    private readonly FrameworkElement[] _pages;
    private readonly RadioButton[] _nav;

    /// <summary>The runners an edit can make stale (their snapshot no longer matches the settings).</summary>
    [Flags]
    private enum Part
    {
        None = 0,
        Mouse = 1,
        Keyboard = 2,
        Camera = 4,
        RandomMove = 8,
        All = Mouse | Keyboard | Camera | RandomMove,
    }

    internal MenuWindow(ClickerService? service, AppSettings settings, StatusSources status)
    {
        InitializeComponent();

        _service = service;
        _settings = settings;
        _status = status;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _debounce.Tick += (_, _) => Flush();
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _poll.Tick += (_, _) => RefreshStatus();

        if (service is not null) _failedHotkeys = service.ApplyHotkeys();

        _pages = [PageMouse, PageKeyboard, PageCamera, PageRandom, PageSettings];
        _nav = [NavMouse, NavKeyboard, NavCamera, NavRandom, NavSettings];

        LoadSettings();
        SelectPage(_settings.MenuPage);
        _loading = false;

        RefreshHotkeyTexts();
        RefreshProblems();
    }

    /// <summary>Any setting changed (the overlay re-reads its own part).</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>The user closed the menu with the X (not with the hotkey); the app keeps running.</summary>
    public event EventHandler? ClosedToTray;

    /// <summary>The user pressed «Выход».</summary>
    public event EventHandler? ExitRequested;

    /// <summary>The window content, detached from the window, for off-screen rendering.</summary>
    internal FrameworkElement DetachContent()
    {
        var root = Root;
        Content = null;
        return root;
    }

    /// <summary>Preview only: pretend these hotkeys could not be registered.</summary>
    internal void ShowFailedHotkeys(IReadOnlyList<string> names)
    {
        _failedHotkeys = names;
        RefreshProblems();
    }

    // ------------------------------------------------------------------
    // Pages
    // ------------------------------------------------------------------

    /// <summary>Shows one page (0 mouse, 1 keyboard, 2 camera, 3 random macro, 4 settings).</summary>
    internal void SelectPage(int index)
    {
        index = Math.Clamp(index, 0, _pages.Length - 1);
        for (int i = 0; i < _pages.Length; i++)
            _pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        _nav[index].IsChecked = true;
    }

    /// <summary>Opens or closes «Дополнительно» on every page (the preview uses it).</summary>
    internal void ExpandAdvanced(bool open)
    {
        foreach (var (toggle, panel) in new[] { (MouseMore, MouseMorePanel), (KeyMore, KeyMorePanel), (CamMore, CamMorePanel), (RndMore, RndMorePanel) })
        {
            toggle.IsChecked = open;
            panel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        int index = Array.IndexOf(_nav, (RadioButton)sender);
        if (index < 0) return;
        SelectPage(index);
        Root.Focus();

        // Remembered between launches; no runner cares, so nothing is marked stale.
        _settings.MenuPage = index;
        Touch();
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        var toggle = (ToggleButton)sender;
        var panel = toggle == MouseMore ? MouseMorePanel
            : toggle == KeyMore ? KeyMorePanel
            : toggle == CamMore ? CamMorePanel
            : RndMorePanel;
        panel.Visibility = toggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------
    // Showing and hiding
    // ------------------------------------------------------------------

    /// <summary>F8: hide if the menu is in front, otherwise bring it up.</summary>
    public void ToggleMenu()
    {
        if (IsVisible && IsActive && WindowState != WindowState.Minimized) HideMenu();
        else ShowMenu();
    }

    public void ShowMenu()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        _foreground.Take(hwnd);
        Activate();
    }

    public void HideMenu()
    {
        Flush();

        // Give the game the foreground back before the menu disappears, rather
        // than letting Windows pick whatever window comes next.
        _foreground.Release();
        Hide();
    }

    /// <summary>
    /// Call before Application.Shutdown: saves pending edits and lets the window
    /// really close instead of hiding.
    /// </summary>
    public void PrepareExit()
    {
        _exiting = true;
        Flush();
    }

    private void OnSourceInitialized(object? sender, EventArgs e) =>
        UiNative.RoundCorners(new WindowInteropHelper(this).Handle);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        HideOrMinimize();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => HideOrMinimize();

    // The close button only hides the menu; the tray icon brings it back even
    // when the menu hotkey could not be registered.
    private void HideOrMinimize()
    {
        HideMenu();
        ClosedToTray?.Invoke(this, EventArgs.Empty);
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            RefreshStatus();
            _poll.Start();
        }
        else
        {
            _poll.Stop();
        }
    }

    // Clicking empty space takes the focus out of a number field or key box.
    private void OnBackgroundMouseDown(object sender, MouseButtonEventArgs e) => Root.Focus();

    // ------------------------------------------------------------------
    // Settings -> controls
    // ------------------------------------------------------------------

    private void LoadSettings()
    {
        var m = _settings.Mouse;
        MouseButtonSeg.SelectedIndex = (int)m.Button;
        MouseModeSeg.SelectedIndex = m.Mode == RunMode.Hold ? 2 : m.DoubleClick ? 1 : 0;
        MouseUnitSeg.SelectedIndex = m.IntervalAsRate ? 1 : 0;
        ShowInterval(MouseInterval, m);
        MouseRandom.Value = m.RandomMs;
        MouseHold.Value = m.HoldMs;
        MousePosSeg.SelectedIndex = m.Position == ClickPosition.Fixed ? 1 : 0;
        MouseDelay.Value = m.StartDelayMs;
        MouseLimit.Value = m.Limit;

        var k = _settings.Keyboard;
        KeyKeyBox.Set(k.VirtualKey, 0);
        KeyCtrl.IsChecked = k.Ctrl;
        KeyShift.IsChecked = k.Shift;
        KeyAlt.IsChecked = k.Alt;
        KeyModeSeg.SelectedIndex = k.Mode == RunMode.Hold ? 1 : 0;
        KeyUnitSeg.SelectedIndex = k.IntervalAsRate ? 1 : 0;
        ShowInterval(KeyInterval, k);
        KeyRandom.Value = k.RandomMs;
        KeyHold.Value = k.HoldMs;
        KeyDelay.Value = k.StartDelayMs;
        KeyLimit.Value = k.Limit;

        var c = _settings.Camera;
        CamDistance.Value = c.DistancePx;
        CamAxisSeg.SelectedIndex = (int)c.Axis;
        CamRandomSwitch.IsChecked = c.RandomDirection;
        CamMove.Value = c.MoveMs;
        CamHold.Value = c.HoldMs;
        CamInterval.Value = c.IntervalMs / 1000.0;
        CamRandom.Value = c.RandomMs / 1000.0;
        CamDelay.Value = c.StartDelayMs;
        CamLimit.Value = c.Limit;

        var r = _settings.RandomMove;
        RndRadius.Value = r.RadiusPx;
        RndMove.Value = r.MoveMs;
        RndPause.Value = r.IntervalMs;
        RndRandom.Value = r.RandomMs;
        RndVerticalSwitch.IsChecked = r.Vertical;
        RndDelay.Value = r.StartDelayMs;

        LockSwitch.IsChecked = _settings.LockToWindow;
        HotMouse.Set(_settings.MouseHotkey.VirtualKey, _settings.MouseHotkey.Modifiers);
        HotKeyboard.Set(_settings.KeyboardHotkey.VirtualKey, _settings.KeyboardHotkey.Modifiers);
        HotMenu.Set(_settings.MenuHotkey.VirtualKey, _settings.MenuHotkey.Modifiers);
        HotCamera.Set(_settings.CameraHotkey.VirtualKey, _settings.CameraHotkey.Modifiers);
        HotRandom.Set(_settings.RandomMoveHotkey.VirtualKey, _settings.RandomMoveHotkey.Modifiers);

        OverlaySwitch.IsChecked = _settings.Overlay.Visible;
        CornerSeg.SelectedIndex = (int)_settings.Overlay.Corner;
        OpacitySlider.Value = Math.Round((1 - Math.Clamp(_settings.Overlay.Opacity, 0.2, 1.0)) * 100);

        RefreshDependentRows();
    }

    /// <summary>Shows IntervalMs in the unit the user prefers; the setting itself stays in ms.</summary>
    private static void ShowInterval(NumberField field, TimingSettings t)
    {
        if (t.IntervalAsRate)
        {
            field.Minimum = 0.01;
            field.Maximum = 1000;
            field.Decimals = 2;
            field.Value = 1000.0 / Math.Max(t.IntervalMs, 1);
        }
        else
        {
            field.Minimum = 1;
            field.Maximum = 3_600_000;
            field.Decimals = 2;
            field.Value = t.IntervalMs;
        }
    }

    private static void ReadInterval(NumberField field, TimingSettings t) =>
        t.IntervalMs = t.IntervalAsRate ? Math.Max(1, 1000.0 / field.Value) : field.Value;

    /// <summary>Rows that mean nothing in the current mode are dimmed; suffixes follow the values.</summary>
    private void RefreshDependentRows()
    {
        var m = _settings.Mouse;
        bool mouseRepeats = m.Mode == RunMode.Repeat;
        MouseIntervalRow.IsEnabled = mouseRepeats;
        MouseRandomRow.IsEnabled = mouseRepeats;
        MouseHoldRow.IsEnabled = mouseRepeats;
        MouseLimitRow.IsEnabled = mouseRepeats;
        MousePointButton.Visibility = m.Position == ClickPosition.Fixed ? Visibility.Visible : Visibility.Collapsed;
        MousePointText.Text = $"{m.FixedX}, {m.FixedY}";
        MouseLimit.Suffix = m.Limit == 0 ? "без лимита" : Plural(m.Limit, "клик", "клика", "кликов");

        var k = _settings.Keyboard;
        bool keyRepeats = k.Mode == RunMode.Repeat;
        KeyIntervalRow.IsEnabled = keyRepeats;
        KeyRandomRow.IsEnabled = keyRepeats;
        KeyHoldRow.IsEnabled = keyRepeats;
        KeyLimitRow.IsEnabled = keyRepeats;
        KeyLimit.Suffix = k.Limit == 0 ? "без лимита" : Plural(k.Limit, "нажатие", "нажатия", "нажатий");

        int camLimit = _settings.Camera.Limit;
        CamLimit.Suffix = camLimit == 0 ? "без лимита" : Plural(camLimit, "сдвиг", "сдвига", "сдвигов");

        CornerRow.IsEnabled = _settings.Overlay.Visible;
        OpacityRow.IsEnabled = _settings.Overlay.Visible;
        OpacityText.Text = $"{OpacitySlider.Value:0}%";
    }

    private static string Plural(int n, string one, string few, string many)
    {
        int tens = n % 100, ones = n % 10;
        if (tens is >= 11 and <= 14) return many;
        return ones == 1 ? one : ones is >= 2 and <= 4 ? few : many;
    }

    // ------------------------------------------------------------------
    // Controls -> settings
    // ------------------------------------------------------------------

    private void OnMouseChanged(object sender, EventArgs e)
    {
        if (_loading) return;
        var m = _settings.Mouse;
        m.Button = (MouseButtonKind)Math.Max(0, MouseButtonSeg.SelectedIndex);
        m.Mode = MouseModeSeg.SelectedIndex == 2 ? RunMode.Hold : RunMode.Repeat;
        m.DoubleClick = MouseModeSeg.SelectedIndex == 1;
        m.RandomMs = (int)MouseRandom.Value;
        m.HoldMs = (int)MouseHold.Value;
        m.Position = MousePosSeg.SelectedIndex == 1 ? ClickPosition.Fixed : ClickPosition.Cursor;
        m.StartDelayMs = (int)MouseDelay.Value;
        m.Limit = (int)MouseLimit.Value;
        Touch(Part.Mouse);
    }

    private void OnMouseIntervalChanged(object sender, EventArgs e)
    {
        if (_loading) return;
        ReadInterval(MouseInterval, _settings.Mouse);
        Touch(Part.Mouse);
    }

    private void OnMouseUnitChanged(object sender, EventArgs e)
    {
        if (_loading) return;
        _settings.Mouse.IntervalAsRate = MouseUnitSeg.SelectedIndex == 1;
        ShowInterval(MouseInterval, _settings.Mouse);
        Touch();
    }

    private void OnKeyChanged(object sender, EventArgs e)
    {
        if (_loading) return;
        var k = _settings.Keyboard;
        k.VirtualKey = KeyKeyBox.VirtualKey;
        k.Ctrl = KeyCtrl.IsChecked == true;
        k.Shift = KeyShift.IsChecked == true;
        k.Alt = KeyAlt.IsChecked == true;
        k.Mode = KeyModeSeg.SelectedIndex == 1 ? RunMode.Hold : RunMode.Repeat;
        k.RandomMs = (int)KeyRandom.Value;
        k.HoldMs = (int)KeyHold.Value;
        k.StartDelayMs = (int)KeyDelay.Value;
        k.Limit = (int)KeyLimit.Value;
        Touch(Part.Keyboard);
    }

    private void OnKeyIntervalChanged(object sender, EventArgs e)
    {
        if (_loading) return;
        ReadInterval(KeyInterval, _settings.Keyboard);
        Touch(Part.Keyboard);
    }

    private void OnKeyUnitChanged(object sender, EventArgs e)
    {
        if (_loading) return;
        _settings.Keyboard.IntervalAsRate = KeyUnitSeg.SelectedIndex == 1;
        ShowInterval(KeyInterval, _settings.Keyboard);
        Touch();
    }

    private void OnCamChanged(object sender, EventArgs e)
    {
        if (_loading) return;
        var c = _settings.Camera;
        c.DistancePx = (int)CamDistance.Value;
        c.Axis = (CameraAxis)Math.Max(0, CamAxisSeg.SelectedIndex);
        c.RandomDirection = CamRandomSwitch.IsChecked == true;
        c.MoveMs = (int)CamMove.Value;
        c.HoldMs = (int)CamHold.Value;

        // Shown in seconds, stored in milliseconds like every other interval.
        c.IntervalMs = Math.Round(CamInterval.Value * 1000);
        c.RandomMs = (int)Math.Round(CamRandom.Value * 1000);
        c.StartDelayMs = (int)CamDelay.Value;
        c.Limit = (int)CamLimit.Value;
        Touch(Part.Camera);
    }

    private void OnRndChanged(object sender, EventArgs e)
    {
        if (_loading) return;
        var r = _settings.RandomMove;
        r.RadiusPx = (int)RndRadius.Value;
        r.MoveMs = (int)RndMove.Value;
        r.IntervalMs = RndPause.Value;
        r.RandomMs = (int)RndRandom.Value;
        r.Vertical = RndVerticalSwitch.IsChecked == true;
        r.StartDelayMs = (int)RndDelay.Value;
        Touch(Part.RandomMove);
    }

    private void OnLockChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.LockToWindow = LockSwitch.IsChecked == true;
        Touch(Part.All);
    }

    private void OnOverlayChanged(object sender, EventArgs e)
    {
        if (_loading) return;
        _settings.Overlay.Visible = OverlaySwitch.IsChecked == true;
        _settings.Overlay.Corner = (OverlayCorner)Math.Max(0, CornerSeg.SelectedIndex);
        _settings.Overlay.Opacity = 1 - OpacitySlider.Value / 100.0;
        Touch();
    }

    private void OnHotkeyChanged(object sender, EventArgs e)
    {
        if (_loading) return;
        _settings.MouseHotkey = new HotkeyBinding(HotMouse.VirtualKey, HotMouse.Modifiers);
        _settings.KeyboardHotkey = new HotkeyBinding(HotKeyboard.VirtualKey, HotKeyboard.Modifiers);
        _settings.MenuHotkey = new HotkeyBinding(HotMenu.VirtualKey, HotMenu.Modifiers);
        _settings.CameraHotkey = new HotkeyBinding(HotCamera.VirtualKey, HotCamera.Modifiers);
        _settings.RandomMoveHotkey = new HotkeyBinding(HotRandom.VirtualKey, HotRandom.Modifiers);
        RefreshHotkeyTexts();
        Touch();
    }

    // While a box listens for a key the global hotkeys are released: otherwise
    // pressing F6 to rebind it would start the clicker instead of reaching the box.
    private void OnCaptureStarted(object? sender, EventArgs e) => _service?.SuspendHotkeys();

    private void OnCaptureEnded(object? sender, EventArgs e)
    {
        if (_service is not null) _failedHotkeys = _service.ApplyHotkeys();
        RefreshProblems();
    }

    private void OnPickPointClick(object sender, RoutedEventArgs e)
    {
        // Out of the way, so the point underneath the menu can be picked too.
        Hide();
        var picker = new PointPickerWindow();
        bool picked = picker.ShowDialog() == true;
        Show();
        Activate();
        if (!picked) return;

        _settings.Mouse.FixedX = picker.PickedX;
        _settings.Mouse.FixedY = picker.PickedY;
        Touch(Part.Mouse);
    }

    /// <summary>
    /// Called after every change: refreshes what depends on it right away, then
    /// schedules the save. <paramref name="stale"/> names the runners whose
    /// snapshot no longer matches.
    /// </summary>
    private void Touch(Part stale = Part.None)
    {
        _stale |= stale;
        RefreshDependentRows();
        RefreshProblems();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Saves now and restarts a running runner whose settings were edited.</summary>
    private void Flush()
    {
        if (!_debounce.IsEnabled) return;
        _debounce.Stop();
        if (_service is null) return;

        try
        {
            _service.Save();
            _saveError = null;
        }
        catch (Exception ex)
        {
            _saveError = $"Не удалось сохранить настройки: {ex.Message}";
        }

        // A runner works from a snapshot taken at Start, so an edit only reaches
        // it through a restart. A runner that isn't running is left alone.
        if (_stale.HasFlag(Part.Mouse)) Restart(_service.Mouse);
        if (_stale.HasFlag(Part.Keyboard)) Restart(_service.Keyboard);
        if (_stale.HasFlag(Part.Camera)) Restart(_service.Camera);
        if (_stale.HasFlag(Part.RandomMove)) Restart(_service.RandomMove);
        _stale = Part.None;

        RefreshProblems();
        RefreshStatus();
    }

    private void Restart(Runner runner)
    {
        if (!runner.IsRunning) return;
        runner.Stop();
        if (_service!.Validate() is null) runner.Start();
    }

    // ------------------------------------------------------------------
    // Start / Stop / Exit
    // ------------------------------------------------------------------

    private void OnMouseStartClick(object sender, RoutedEventArgs e)
    {
        Flush();
        _service?.ToggleMouse();
        RefreshStatus();
    }

    private void OnKeyStartClick(object sender, RoutedEventArgs e)
    {
        Flush();
        _service?.ToggleKeyboard();
        RefreshStatus();
    }

    private void OnCamStartClick(object sender, RoutedEventArgs e)
    {
        Flush();
        _service?.ToggleCamera();
        RefreshStatus();
    }

    private void OnRndStartClick(object sender, RoutedEventArgs e)
    {
        Flush();
        _service?.ToggleRandomMove();
        RefreshStatus();
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => ExitRequested?.Invoke(this, EventArgs.Empty);

    // ------------------------------------------------------------------
    // Live state
    // ------------------------------------------------------------------

    /// <summary>
    /// Status lines, sidebar dots and the Start/Stop buttons. Cheap; called from
    /// a timer. While the settings are unusable the status line says why, since
    /// that is exactly when the Start button is switched off.
    /// </summary>
    public void RefreshStatus()
    {
        string? invalid = _service?.Validate();
        var idle = (Brush)FindResource("TextFaint");
        var running = (Brush)FindResource("Success");
        var paused = (Brush)FindResource("Warning");
        var muted = (Brush)FindResource("TextMuted");
        var danger = (Brush)FindResource("Danger");
        bool keyProblem = IsKeyProblem(invalid);

        var mouse = _status.Mouse();
        Show(mouse, mouse.Describe("к/с", "Остановлено", "Кнопка зажата"), false,
            MouseDot, NavMouseDot, MouseStatus, MouseStartButton, MouseStartText, "Старт");
        var key = _status.Keyboard();
        Show(key, key.Describe("н/с", "Остановлено", "Клавиша зажата"), keyProblem,
            KeyDot, NavKeyboardDot, KeyStatus, KeyStartButton, KeyStartText, "Старт");
        var camera = _status.Camera();
        Show(camera, camera.Describe("", "Остановлено"), false,
            CamDot, NavCameraDot, CamStatus, CamStartButton, CamStartText, "Старт");
        var random = _status.RandomMove();
        Show(random, random.Describe("", "Остановлено"), false,
            RndDot, NavRandomDot, RndStatus, RndStartButton, RndStartText, "Случайный макрос");

        void Show(RunnerStatus status, string text, bool ownProblem, Ellipse dot, Ellipse navDot, TextBlock line,
            Button button, TextBlock caption, string startCaption)
        {
            bool blocked = !status.Running && invalid is not null;
            var color = blocked ? danger : status.Color(idle, running, paused);
            dot.Fill = color;
            line.Text = blocked ? invalid : Capitalize(text);
            line.Foreground = blocked ? danger : status.Color(muted, running, paused);

            // In the sidebar red marks only the entry the problem belongs to.
            navDot.Fill = !status.Running && ownProblem ? danger : status.Color(idle, running, paused);

            caption.Text = status.Running ? "Стоп" : startCaption;
            button.Tag = status.Running ? "stop" : null;
            button.IsEnabled = status.Running || invalid is null;
        }
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0]) + text[1..];

    private static bool IsKeyProblem(string? invalid) => invalid is not null && invalid.Contains("автонажати");

    /// <summary>Hotkeys as shown next to the sidebar entries and on the Start buttons.</summary>
    private void RefreshHotkeyTexts()
    {
        Set(_settings.MouseHotkey, MouseStartKey, MouseStartCap, NavMouseKey);
        Set(_settings.KeyboardHotkey, KeyStartKey, KeyStartCap, NavKeyboardKey);
        Set(_settings.CameraHotkey, CamStartKey, CamStartCap, NavCameraKey);
        Set(_settings.RandomMoveHotkey, RndStartKey, RndStartCap, NavRandomKey);

        static void Set(HotkeyBinding binding, TextBlock onButton, Border cap, TextBlock inSidebar)
        {
            string text = binding.VirtualKey == 0 ? "" : KeyNames.Combo(binding);
            onButton.Text = text;
            cap.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            inSidebar.Text = text;
        }
    }

    /// <summary>
    /// Red marks where a problem belongs: the key box on the keyboard page, the
    /// hotkey boxes and a line of text on the settings page, and a red dot on
    /// «Настройки» in the sidebar so it is noticed from any page.
    /// </summary>
    private void RefreshProblems()
    {
        string? invalid = _service?.Validate();
        bool clash = invalid == "Две горячие клавиши совпадают.";

        KeyKeyBox.HasError = IsKeyProblem(invalid);
        HotMouse.HasError = clash || _failedHotkeys.Contains(ClickerService.MouseHotkeyName);
        HotKeyboard.HasError = clash || _failedHotkeys.Contains(ClickerService.KeyboardHotkeyName);
        HotMenu.HasError = clash || _failedHotkeys.Contains(ClickerService.MenuHotkeyName);
        HotCamera.HasError = clash || _failedHotkeys.Contains(ClickerService.CameraHotkeyName);
        HotRandom.HasError = clash || _failedHotkeys.Contains(ClickerService.RandomMoveHotkeyName);

        string? problem = (clash ? invalid : null) ?? DescribeFailedHotkeys() ?? _saveError;
        SettingsProblem.Text = problem ?? "";
        SettingsProblem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        NavSettingsDot.Fill = problem is null ? Brushes.Transparent : (Brush)FindResource("Danger");

        string menuKey = KeyNames.Combo(_settings.MenuHotkey);
        bool menuKeyWorks = _settings.MenuHotkey.VirtualKey != 0 && !_failedHotkeys.Contains(ClickerService.MenuHotkeyName);
        SidebarHint.Text = menuKeyWorks ? $"{menuKey} — скрыть / показать" : "Меню — значок в трее";
        SidebarHint.ToolTip = "Крестик только прячет меню — автокликер продолжает работать. Значок в трее вернёт меню.";

        RefreshStatus();
    }

    private string? DescribeFailedHotkeys()
    {
        if (_failedHotkeys.Count == 0) return null;

        var names = new List<string>(5);
        foreach (var name in _failedHotkeys)
        {
            var (binding, what) = name switch
            {
                ClickerService.MouseHotkeyName => (_settings.MouseHotkey, "мышь"),
                ClickerService.KeyboardHotkeyName => (_settings.KeyboardHotkey, "клавиатура"),
                ClickerService.CameraHotkeyName => (_settings.CameraHotkey, "камера"),
                ClickerService.RandomMoveHotkeyName => (_settings.RandomMoveHotkey, "случайный макрос"),
                _ => (_settings.MenuHotkey, "меню"),
            };
            names.Add(binding.VirtualKey == 0 ? $"«{what}» не задана" : $"{KeyNames.Combo(binding)} ({what})");
        }

        return $"Не работает: {string.Join(", ", names)}. Возможно, клавишу заняла другая программа — выберите другую.";
    }
}
