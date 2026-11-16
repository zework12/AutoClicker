using System.Windows;
using System.Windows.Threading;
using AutoClicker.Core;
using AutoClicker.Preview;
using AutoClicker.Services;
using AutoClicker.Views;

namespace AutoClicker;

public partial class App : Application
{
    private const string MutexName = @"Local\AutoClicker.Instance.4f6c1d0a";
    private const string ShowMenuEventName = @"Local\AutoClicker.ShowMenu.4f6c1d0a";

    private Mutex? _instance;
    private EventWaitHandle? _showMenu;
    private RegisteredWaitHandle? _showMenuWait;
    private ClickerService? _service;
    private MenuWindow? _menu;
    private OverlayWindow? _overlay;
    private TrayIconService? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Hidden switch: draw the UI into PNG files and leave. Nothing else starts.
        int flag = Array.IndexOf(e.Args, "--render-preview");
        if (flag >= 0)
        {
            Shutdown(PreviewRenderer.Run(flag + 1 < e.Args.Length ? e.Args[flag + 1] : "."));
            return;
        }

        _instance = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Already running: ask that copy to bring its menu up, then leave.
            _instance.Dispose();
            _instance = null;
            if (EventWaitHandle.TryOpenExisting(ShowMenuEventName, out var running))
            {
                running.Set();
                running.Dispose();
            }
            Shutdown();
            return;
        }

        _service = new ClickerService();
        var service = _service;
        var settings = service.Settings;
        var nudges = new NudgeClock();
        var status = new StatusSources(
            () => RunnerStatus.Of(service.Mouse, settings.Mouse),
            () => RunnerStatus.Of(service.Keyboard, settings.Keyboard),
            () => RunnerStatus.OfCamera(service.Camera, settings.Camera, nudges),
            () => RunnerStatus.OfRandomMove(service.RandomMove));

        _overlay = new OverlayWindow(settings, status);
        _menu = new MenuWindow(service, settings, status);
        _menu.SettingsChanged += (_, _) => _overlay.ApplySettings();
        _menu.ExitRequested += (_, _) => Quit();

        // Created only here, on the normal start path: the preview switch and a
        // second copy leave before this point and never touch the tray.
        _tray = new TrayIconService();
        _tray.MenuRequested += (_, _) => _menu.ShowMenu();
        _tray.StopAllRequested += (_, _) =>
        {
            service.StopAll();
            RefreshState();
        };
        _tray.ExitRequested += (_, _) => Quit();
        _menu.ClosedToTray += (_, _) => _tray.ShowClosedHintOnce();

        service.MenuRequested += (_, _) => _menu.ToggleMenu();

        // Raised on a runner's worker thread. BeginInvoke, never Invoke: Stop()
        // joins that thread from the UI thread, and a blocking call would deadlock.
        service.StateChanged += (_, _) => Dispatcher.BeginInvoke(RefreshState);

        // If anything goes wrong on the UI thread, never leave a button held down.
        DispatcherUnhandledException += (_, _) => _service?.StopAll();

        _showMenu = new EventWaitHandle(false, EventResetMode.AutoReset, ShowMenuEventName);
        _showMenuWait = ThreadPool.RegisterWaitForSingleObject(
            _showMenu,
            (_, _) => Dispatcher.BeginInvoke(() => _menu?.ShowMenu()),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        _overlay.ApplySettings();
        _menu.ShowMenu();
    }

    private void Quit()
    {
        _menu?.PrepareExit();
        Shutdown();
    }

    private void RefreshState()
    {
        _overlay?.Refresh();
        _menu?.RefreshStatus();

        if (_tray is null || _service is null) return;
        var running = new List<string>(4);
        if (_service.Mouse.IsRunning) running.Add("мышь");
        if (_service.Keyboard.IsRunning) running.Add("клавиатура");
        if (_service.Camera.IsRunning) running.Add("камера");
        if (_service.RandomMove.IsRunning) running.Add("макрос");
        _tray.SetRunning(running);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Log-off or shutdown closes the windows for us; make sure the menu
        // closes instead of hiding and the last edits are on disk.
        _menu?.PrepareExit();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showMenuWait?.Unregister(null);
        _showMenu?.Dispose();

        // Stops the runners (releasing anything held) and frees the hotkeys.
        _service?.Dispose();

        // Last, so the icon is there for as long as anything could still be running;
        // disposing it also removes it from the tray instead of leaving a ghost.
        _tray?.Dispose();

        if (_instance is not null)
        {
            _instance.ReleaseMutex();
            _instance.Dispose();
        }

        base.OnExit(e);
    }
}
