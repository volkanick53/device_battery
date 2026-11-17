namespace DeviceBattery;

public enum ConnectionType
{
    Bluetooth,
    Usb,
    Other
}

public enum DeviceKind
{
    Other,
    Keyboard,
    Mouse,
    Headphones,
    Controller
}

public sealed record DeviceBatteryInfo(
    string Id,
    string DeviceName,
    int BatteryPercentage,
    ConnectionType ConnectionType,
    bool IsConnected,
    DeviceKind Kind = DeviceKind.Other);

public interface IBatteryProvider
{
    /// <summary>Synchronous scan; called from a worker thread. Must not throw.</summary>
    IReadOnlyList<DeviceBatteryInfo> GetBatteries();
}

public static class DeviceKindGuesser
{
    /// <summary>Best-effort kind from the device name (fallback when PnP classes say nothing).</summary>
    public static DeviceKind FromName(string name)
    {
        string n = name.ToLowerInvariant();
        if (Has(n, "keyboard", "klavye", "keys", "k380", "k780", "k400")) return DeviceKind.Keyboard;
        if (Has(n, "mouse", "fare", "master", "anywhere", "g102", "g502", "g305", "g703", "pebble", "m720", "m590"))
            return DeviceKind.Mouse;
        if (Has(n, "headset", "headphone", "kulaklık", "kulaklik", "buds", "airpods", "arctis", "wh-", "wf-", "earbud", "soundcore"))
            return DeviceKind.Headphones;
        if (Has(n, "xbox", "controller", "gamepad", "dualsense", "dualshock", "joy-con", "pro controller"))
            return DeviceKind.Controller;
        return DeviceKind.Other;
    }

    private static bool Has(string s, params string[] needles) => needles.Any(s.Contains);
}
