using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoClicker.Core;

/// <summary>
/// The whole engine behind one object: settings, the two runners and global
/// hotkeys. Create once on the UI thread (App.OnStartup) and Dispose on exit.
/// </summary>
public sealed class ClickerService : IDisposable
{
    public const string MouseHotkeyName = "mouse";
    public const string KeyboardHotkeyName = "keyboard";
    public const string MenuHotkeyName = "menu";
    public const string CameraHotkeyName = "camera";
    public const string RandomMoveHotkeyName = "random";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HotkeyManager _hotkeys = new();

    public AppSettings Settings { get; private set; }
    public MouseRunner Mouse { get; }
    public KeyRunner Keyboard { get; }
    public CameraRunner Camera { get; }
    public RandomMoveRunner RandomMove { get; }

    /// <summary>Path of settings.json (next to the exe, or %LOCALAPPDATA%\AutoClicker if that is read-only).</summary>
    public string SettingsPath { get; }

    /// <summary>The menu hotkey was pressed. UI thread.</summary>
    public event EventHandler? MenuRequested;

    /// <summary>Mouse or keyboard runner started, stopped, paused or resumed. Worker thread — use Dispatcher.BeginInvoke, never Invoke.</summary>
    public event EventHandler? StateChanged;

    public ClickerService()
    {
        SettingsPath = ResolveSettingsPath();
        Settings = Load(SettingsPath);
        Mouse = new MouseRunner(() => Settings.Mouse) { LockToWindow = () => Settings.LockToWindow };
        Keyboard = new KeyRunner(() => Settings.Keyboard) { LockToWindow = () => Settings.LockToWindow };
        Mouse.Changed += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
        Keyboard.Changed += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
        Camera = new CameraRunner(() => Settings.Camera) { LockToWindow = () => Settings.LockToWindow };
        Camera.Changed += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
        RandomMove = new RandomMoveRunner(() => Settings.RandomMove) { LockToWindow = () => Settings.LockToWindow };
        RandomMove.Changed += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
        _hotkeys.Pressed += OnHotkey;
        ApplyHotkeys();
    }

    /// <summary>
    /// (Re)registers the three hotkeys from Settings. Returns the names that
    /// failed (taken by another program or empty) — show them to the user.
    /// </summary>
    public IReadOnlyList<string> ApplyHotkeys()
    {
        _hotkeys.UnregisterAll();
        var failed = new List<string>();
        if (!_hotkeys.Register(MouseHotkeyName, Settings.MouseHotkey)) failed.Add(MouseHotkeyName);
        if (!_hotkeys.Register(KeyboardHotkeyName, Settings.KeyboardHotkey)) failed.Add(KeyboardHotkeyName);
        if (!_hotkeys.Register(MenuHotkeyName, Settings.MenuHotkey)) failed.Add(MenuHotkeyName);
        if (!_hotkeys.Register(CameraHotkeyName, Settings.CameraHotkey)) failed.Add(CameraHotkeyName);
        if (!_hotkeys.Register(RandomMoveHotkeyName, Settings.RandomMoveHotkey)) failed.Add(RandomMoveHotkeyName);
        return failed;
    }

    /// <summary>
    /// Releases all three hotkeys while the menu captures a key, so pressing a
    /// currently bound key reaches the capture box instead of toggling a runner.
    /// Call <see cref="ApplyHotkeys"/> to bring them back.
    /// </summary>
    public void SuspendHotkeys() => _hotkeys.UnregisterAll();

    /// <summary>
    /// Null if the settings are usable, otherwise a Russian message for the UI.
    /// The key to auto-press must not be one of our own hotkeys, or every
    /// synthetic press would toggle the runner.
    /// </summary>
    public string? Validate()
    {
        int key = Settings.Keyboard.VirtualKey;
        if (key == 0) return "Не выбрана клавиша для автонажатия.";
        var all = new[] { Settings.MouseHotkey, Settings.KeyboardHotkey, Settings.MenuHotkey, Settings.CameraHotkey, Settings.RandomMoveHotkey };
        foreach (var hk in all)
            if (hk.VirtualKey == key) return "Клавиша автонажатия совпадает с горячей клавишей — выберите другую.";
        for (int i = 0; i < all.Length; i++)
            for (int j = i + 1; j < all.Length; j++)
                if (all[i].VirtualKey == all[j].VirtualKey && all[i].Modifiers == all[j].Modifiers)
                    return "Две горячие клавиши совпадают.";
        if (Settings.Camera.DistancePx <= 0) return "Сдвиг камеры должен быть больше нуля.";
        if (Settings.RandomMove.RadiusPx <= 0) return "Размах случайного макроса должен быть больше нуля.";
        return null;
    }

    public void ToggleCamera()
    {
        if (!Camera.IsRunning && Validate() is not null) return;
        Camera.Toggle();
    }

    public void ToggleRandomMove()
    {
        if (!RandomMove.IsRunning && Validate() is not null) return;
        RandomMove.Toggle();
    }

    public void ToggleMouse()
    {
        if (!Mouse.IsRunning && Validate() is not null) return;
        Mouse.Toggle();
    }

    public void ToggleKeyboard()
    {
        if (!Keyboard.IsRunning && Validate() is not null) return;
        Keyboard.Toggle();
    }

    public void StopAll()
    {
        Mouse.Stop();
        Keyboard.Stop();
        Camera.Stop();
        RandomMove.Stop();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Settings, Json));
    }

    private void OnHotkey(object? sender, string name)
    {
        switch (name)
        {
            case MouseHotkeyName: ToggleMouse(); break;
            case KeyboardHotkeyName: ToggleKeyboard(); break;
            case CameraHotkeyName: ToggleCamera(); break;
            case RandomMoveHotkeyName: ToggleRandomMove(); break;
            case MenuHotkeyName: MenuRequested?.Invoke(this, EventArgs.Empty); break;
        }
    }

    private static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
        }
        catch (Exception)
        {
            // Corrupt file: start from defaults rather than refusing to run.
        }
        return new AppSettings();
    }

    private static string ResolveSettingsPath()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "settings.json");
        try
        {
            var probe = Path.Combine(AppContext.BaseDirectory, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return local;
        }
        catch (Exception)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoClicker", "settings.json");
        }
    }

    public void Dispose()
    {
        StopAll();
        _hotkeys.Dispose();
    }
}
