using System.Windows;
using AutoClicker.Interop;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace AutoClicker.Services;

/// <summary>
/// The tray icon: the permanent sign that the autoclicker is still running
/// after its menu was closed, and a way out that does not need the menu.
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    private const int TooltipLimit = 63;   // NotifyIcon refuses longer texts on older Windows

    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _stopAllItem;
    private readonly Drawing.Icon _idleIcon;
    private readonly Drawing.Icon _runningIcon;
    private readonly IntPtr _runningIconHandle;
    private bool _anythingRunning;
    private bool _hintShown;
    private bool _disposed;

    public TrayIconService()
    {
        _idleIcon = LoadIcon();
        (_runningIcon, _runningIconHandle) = WithGreenDot(_idleIcon);

        _stopAllItem = new Forms.ToolStripMenuItem("Остановить всё", null, (_, _) => Raise(StopAllRequested));

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("Открыть меню", null, (_, _) => Raise(MenuRequested)));
        menu.Items.Add(_stopAllItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Выход", null, (_, _) => Raise(ExitRequested)));
        menu.Opening += (_, _) => _stopAllItem.Enabled = _anythingRunning;

        _icon = new Forms.NotifyIcon
        {
            Icon = _idleIcon,
            ContextMenuStrip = menu,
            Visible = true,
        };

        // A plain left click is enough; the second click of a double click
        // just asks for the same menu again.
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) Raise(MenuRequested);
        };

        SetRunning([]);
    }

    public event EventHandler? MenuRequested;

    public event EventHandler? StopAllRequested;

    public event EventHandler? ExitRequested;

    /// <summary>
    /// Updates the tooltip and the icon. <paramref name="names"/> are the running
    /// runners in lower case ("мышь", "камера"); empty when nothing runs.
    /// </summary>
    public void SetRunning(IReadOnlyList<string> names)
    {
        if (_disposed) return;

        _anythingRunning = names.Count > 0;
        string text = _anythingRunning
            ? "Автокликер — работает: " + string.Join(", ", names)
            : "Автокликер — ничего не запущено";
        if (text.Length > TooltipLimit) text = text[..(TooltipLimit - 1)] + "…";

        if (_icon.Text != text) _icon.Text = text;
        var icon = _anythingRunning ? _runningIcon : _idleIcon;
        if (!ReferenceEquals(_icon.Icon, icon)) _icon.Icon = icon;
    }

    /// <summary>Tells the user, once per launch, where the program went when the menu was closed.</summary>
    public void ShowClosedHintOnce()
    {
        if (_hintShown || _disposed) return;
        _hintShown = true;
        _icon.ShowBalloonTip(
            5000,
            "Автокликер",
            "Автокликер работает в трее. Выход — правый клик по значку.",
            Forms.ToolTipIcon.None);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Without hiding it first the icon lingers in the tray until hovered.
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _runningIcon.Dispose();
        _idleIcon.Dispose();
        if (_runningIconHandle != IntPtr.Zero) UiNative.DestroyIcon(_runningIconHandle);
    }

    private static Drawing.Icon LoadIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/app.ico"));
        if (resource is null) return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();

        using var stream = resource.Stream;

        // Picking the small size explicitly keeps the tray from rescaling a 256px frame.
        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    /// <summary>
    /// The same icon with a green dot in the bottom right corner, shown while
    /// anything is running. Drawn at the icon's own pixel size so it stays crisp.
    /// The handle is returned because an icon made from a bitmap must be freed by hand.
    /// </summary>
    private static (Drawing.Icon Icon, IntPtr Handle) WithGreenDot(Drawing.Icon source)
    {
        using var bitmap = source.ToBitmap();
        int size = bitmap.Width;

        // Dot ~45% of the icon with a dark ring so it reads on any taskbar colour.
        float dot = MathF.Max(6, MathF.Round(size * 0.45f));
        float ring = MathF.Max(1, MathF.Round(size / 16f));
        float outer = dot + ring * 2;
        float origin = size - outer;

        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = Drawing.Drawing2D.PixelOffsetMode.Half;
            using var ringBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x0B, 0x0E, 0x14));
            using var dotBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x4C, 0xCB, 0x7C));
            g.FillEllipse(ringBrush, origin, origin, outer, outer);
            g.FillEllipse(dotBrush, origin + ring, origin + ring, dot, dot);
        }

        IntPtr handle = bitmap.GetHicon();
        return (Drawing.Icon.FromHandle(handle), handle);
    }

    private void Raise(EventHandler? handler) => handler?.Invoke(this, EventArgs.Empty);
}
