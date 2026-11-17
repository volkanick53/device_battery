using Microsoft.Win32;

namespace DeviceBattery;

/// <summary>
/// User settings, persisted in HKCU\Software\DeviceBattery (no files, removed again by the uninstaller).
/// Every setter writes through immediately; reads are cached after the first access.
/// </summary>
internal static class Settings
{
    private const string KeyPath = @"Software\DeviceBattery";

    private static bool? _taskbarWidget;
    private static bool? _notifyLow;
    private static int? _intervalMinutes;
    private static HashSet<string>? _hidden;

    /// <summary>true = readout on the taskbar (default), false = one tray icon per device.</summary>
    public static bool TaskbarWidget
    {
        get => _taskbarWidget ??= ReadInt("TaskbarWidget", 1) != 0;
        set { _taskbarWidget = value; WriteInt("TaskbarWidget", value ? 1 : 0); }
    }

    /// <summary>Show a notification when a device drops to 15% or below.</summary>
    public static bool NotifyLow
    {
        get => _notifyLow ??= ReadInt("NotifyLow", 1) != 0;
        set { _notifyLow = value; WriteInt("NotifyLow", value ? 1 : 0); }
    }

    public static readonly int[] IntervalChoices = { 1, 3, 5, 10, 30 };

    public static int IntervalMinutes
    {
        get
        {
            int v = _intervalMinutes ??= ReadInt("RefreshMinutes", 3);
            return Array.IndexOf(IntervalChoices, v) >= 0 ? v : 3;
        }
        set { _intervalMinutes = value; WriteInt("RefreshMinutes", value); }
    }

    public static bool IsHidden(string deviceId) => Hidden.Contains(deviceId);

    public static void SetHidden(string deviceId, bool hidden)
    {
        if (hidden ? Hidden.Add(deviceId) : Hidden.Remove(deviceId))
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
                k.SetValue("HiddenDevices", Hidden.ToArray(), RegistryValueKind.MultiString);
            }
            catch { /* registry blocked by policy: keep in memory for this session */ }
        }
    }

    private static HashSet<string> Hidden => _hidden ??= LoadHidden();

    private static HashSet<string> LoadHidden()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (k?.GetValue("HiddenDevices") is string[] ids)
                return new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static int ReadInt(string name, int fallback)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
            return k?.GetValue(name) is int v ? v : fallback;
        }
        catch { return fallback; }
    }

    private static void WriteInt(string name, int value)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            k.SetValue(name, value, RegistryValueKind.DWord);
        }
        catch { /* registry blocked by policy: keep in memory for this session */ }
    }
}
