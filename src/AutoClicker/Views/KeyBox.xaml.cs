using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AutoClicker.Core;

namespace AutoClicker.Views;

public enum KeyBoxKind
{
    /// <summary>A global hotkey: one key plus optional Ctrl/Shift/Alt.</summary>
    Hotkey,

    /// <summary>One key on its own; a modifier counts as a key (e.g. hold Shift).</summary>
    SingleKey,
}

/// <summary>
/// A field that records the next key the user presses. It only listens to
/// PreviewKeyDown while it has the keyboard focus inside our own window — no
/// hooks of any kind.
/// </summary>
public partial class KeyBox : UserControl
{
    private bool _capturing;
    private bool _hasError;
    private string? _notice;

    public KeyBox()
    {
        InitializeComponent();
        UpdateDisplay();
    }

    public KeyBoxKind Kind { get; set; }

    public int VirtualKey { get; private set; }

    /// <summary>MOD_* flags. Always 0 for <see cref="KeyBoxKind.SingleKey"/>.</summary>
    public uint Modifiers { get; private set; }

    /// <summary>Draws the field in red, e.g. when the hotkey is taken by another program.</summary>
    public bool HasError
    {
        get => _hasError;
        set
        {
            _hasError = value;
            UpdateDisplay();
        }
    }

    /// <summary>The user picked a new key.</summary>
    public event EventHandler? Changed;

    /// <summary>Capture began: the owner should release global hotkeys so their keys can be typed here.</summary>
    public event EventHandler? CaptureStarted;

    public event EventHandler? CaptureEnded;

    public void Set(int virtualKey, uint modifiers)
    {
        VirtualKey = virtualKey;
        Modifiers = modifiers;
        UpdateDisplay();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true;
        if (_capturing) return;

        _capturing = true;
        _notice = null;
        Keyboard.Focus(this);
        UpdateDisplay();
        CaptureStarted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_capturing)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;

        // Alt arrives as Key.System with the real key in SystemKey; an active
        // input method hides it in ImeProcessedKey.
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };

        if (key == Key.Escape)
        {
            StopCapture();
            return;
        }

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        if (Kind == KeyBoxKind.SingleKey)
        {
            Accept(vk, 0);
            return;
        }

        if (IsModifier(key)) return;

        uint mods = 0;
        var held = Keyboard.Modifiers;
        if (held.HasFlag(ModifierKeys.Alt)) mods |= Native.MOD_ALT;
        if (held.HasFlag(ModifierKeys.Control)) mods |= Native.MOD_CONTROL;
        if (held.HasFlag(ModifierKeys.Shift)) mods |= Native.MOD_SHIFT;

        if (mods == 0 && NeedsModifier(vk))
        {
            // A bare letter as a global hotkey would be swallowed everywhere,
            // including inside the game.
            _notice = "нужен Ctrl, Alt или Shift";
            UpdateDisplay();
            return;
        }

        Accept(vk, mods);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        StopCapture();
    }

    private void Accept(int vk, uint mods)
    {
        bool changed = vk != VirtualKey || mods != Modifiers;
        VirtualKey = vk;
        Modifiers = mods;
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
        StopCapture();
    }

    private void StopCapture()
    {
        if (!_capturing) return;
        _capturing = false;
        _notice = null;
        UpdateDisplay();
        CaptureEnded?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsModifier(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin;

    /// <summary>Typing keys: letters, digits, space, Enter, Tab, Backspace, arrows, punctuation.</summary>
    private static bool NeedsModifier(int vk) =>
        vk is >= 0x30 and <= 0x5A or 0x20 or 0x0D or 0x09 or 0x08 or >= 0x25 and <= 0x28 or >= 0xBA and <= 0xE2;

    private void UpdateDisplay()
    {
        if (Display is null) return;

        if (_capturing)
        {
            Display.Text = _notice ?? "нажмите клавишу…";
            Display.Foreground = (Brush)FindResource("TextMuted");
            Chrome.BorderBrush = (Brush)FindResource("Accent");
            return;
        }

        Display.Text = KeyNames.Combo(VirtualKey, Modifiers);
        Display.Foreground = (Brush)FindResource(VirtualKey == 0 ? "TextMuted" : "TextPrimary");
        if (_hasError) Chrome.BorderBrush = (Brush)FindResource("Danger");
        else Chrome.ClearValue(Border.BorderBrushProperty);
    }
}
