using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AutoClicker.Core;
using AutoClicker.Views;

namespace AutoClicker.Preview;

/// <summary>
/// Backs the hidden <c>--render-preview &lt;dir&gt;</c> switch: draws the menu and
/// the overlay into PNG files without showing a window, creating the engine,
/// registering a hotkey or sending any input. Used to check the look of the UI.
/// </summary>
internal static class PreviewRenderer
{
    /// <summary>Returns the process exit code; on failure the reason is in error.txt.</summary>
    public static int Run(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            var idle = new RunnerStatus(false, false, false, 0, 0);
            var clicking = new RunnerStatus(true, false, false, 1532, 9.8);
            var pressing = new RunnerStatus(true, false, false, 48, 2.0);
            var paused = new RunnerStatus(true, true, false, 48, 0);
            var nudging = new RunnerStatus(true, false, false, 12, 0, "через ~18 с · 12");
            var wandering = new RunnerStatus(true, false, false, 214, 0, "работает · 214");

            var allIdle = new StatusSources(() => idle, () => idle, () => idle, () => idle);
            var allBusy = new StatusSources(() => clicking, () => pressing, () => nudging, () => wandering);
            var mixed = new StatusSources(() => clicking, () => paused, () => nudging, () => wandering);
            var pausedMix = new StatusSources(() => idle, () => paused, () => paused, () => idle);

            // Every page as it looks the first time, with «Дополнительно» closed.
            var defaults = new AppSettings();
            string[] pages = ["mouse", "keyboard", "camera", "random", "settings"];
            for (int i = 0; i < pages.Length; i++)
            {
                var page = new MenuWindow(null, defaults, allIdle);
                page.SelectPage(i);
                Save(page.DetachContent(), directory, "menu-" + pages[i], null);
            }

            // Everything switched on: runners going, «Дополнительно» open, a fixed
            // point, hold mode on the keyboard side and a hotkey that failed.
            var busy = new AppSettings();
            busy.Mouse.Position = ClickPosition.Fixed;
            busy.Mouse.FixedX = 1280;
            busy.Mouse.FixedY = 720;
            busy.Mouse.IntervalAsRate = true;
            busy.Mouse.RandomMs = 15;
            busy.Mouse.Limit = 5000;
            busy.Keyboard.Mode = RunMode.Hold;
            busy.Keyboard.Shift = true;
            busy.Keyboard.VirtualKey = 0x57;
            busy.Camera.Axis = CameraAxis.Both;
            busy.Camera.Limit = 3;
            busy.RandomMove.Vertical = false;
            busy.Overlay.Corner = OverlayCorner.BottomLeft;
            busy.Overlay.Opacity = 0.6;
            for (int i = 0; i < pages.Length; i++)
            {
                var page = new MenuWindow(null, busy, mixed);
                page.ShowFailedHotkeys([ClickerService.KeyboardHotkeyName]);
                page.ExpandAdvanced(true);
                page.SelectPage(i);
                Save(page.DetachContent(), directory, "menu-" + pages[i] + "-running", null);
            }

            // Overlay over a mid-tone backdrop, the way it sits on a game.
            var backdrop = new LinearGradientBrush(Color.FromRgb(0x5B, 0x7A, 0x4E), Color.FromRgb(0x2C, 0x3F, 0x59), 20);
            var sample = new AppSettings();
            sample.Keyboard.Ctrl = true;
            Save(new OverlayWindow(sample, allBusy).DetachContent(), directory, "overlay", backdrop);
            Save(new OverlayWindow(sample, pausedMix).DetachContent(), directory, "overlay-paused", backdrop);
            Save(new OverlayWindow(defaults, allIdle).DetachContent(), directory, "overlay-idle", backdrop);
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(directory, "error.txt"), ex.ToString()); }
            catch (Exception) { /* nowhere left to report to */ }
            return 1;
        }
    }

    /// <summary>Lays the element out off-screen and writes name.png (100%) and name@2x.png (200%).</summary>
    private static void Save(FrameworkElement root, string directory, string name, Brush? backdrop)
    {
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();

        // Let bindings and templates settle before the snapshot.
        root.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        root.UpdateLayout();

        double pad = backdrop is null ? 0 : 24;
        double width = Math.Ceiling(root.ActualWidth + pad * 2);
        double height = Math.Ceiling(root.ActualHeight + pad * 2);

        var scene = new DrawingVisual();
        using (var dc = scene.RenderOpen())
        {
            if (backdrop is not null) dc.DrawRectangle(backdrop, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(
                new VisualBrush(root) { Stretch = Stretch.None },
                null,
                new Rect(pad, pad, root.ActualWidth, root.ActualHeight));
        }

        foreach (var (scale, suffix) in new[] { (1, ""), (2, "@2x") })
        {
            var bitmap = new RenderTargetBitmap((int)width * scale, (int)height * scale, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(scene);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, name + suffix + ".png"));
            encoder.Save(file);
        }
    }
}
