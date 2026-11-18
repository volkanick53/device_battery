using System.Text;
using static DeviceBattery.NativeMethods;

namespace DeviceBattery;

/// <summary>
/// Reads DEVPKEY_Device_BatteryLife ({104EA319-6EE2-4701-BD47-8DDBF425BBE5}, 2) from every
/// present PnP node via SetupAPI. Windows publishes this for Bluetooth LE/Classic HID
/// peripherals that expose the standard Battery Service. Multiple nodes belonging to one
/// physical device are collapsed using the device ContainerId.
/// </summary>
public sealed class PnpBatteryProvider : IBatteryProvider
{
    private static readonly DEVPROPKEY BatteryLife = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
    private static readonly DEVPROPKEY IsConnectedKey = new("83DA6326-97A6-4088-9453-A1923F573B29", 15);
    private static readonly DEVPROPKEY FriendlyName = new("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
    private static readonly DEVPROPKEY DeviceDesc = new("A45C254E-DF1C-4EFD-8020-67D146A850E0", 2);
    private static readonly DEVPROPKEY BusReportedDesc = new("540B947E-8B40-45BC-A8A2-6A0B894CBDA2", 4);
    private static readonly DEVPROPKEY ContainerIdKey = new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C", 2);
    private static readonly DEVPROPKEY ClassKey = new("A45C254E-DF1C-4EFD-8020-67D146A850E0", 9);

    public IReadOnlyList<DeviceBatteryInfo> GetBatteries()
    {
        var byContainer = new Dictionary<string, DeviceBatteryInfo>();
        var classesByContainer = new Dictionary<string, HashSet<string>>();
        var connectedContainers = new HashSet<string>();
        var connectedKeys = new HashSet<string>(); // battery nodes without a ContainerId that are connected
        IntPtr set = IntPtr.Zero;
        try
        {
            set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_ALLCLASSES | DIGCF_PRESENT);
            if (set == InvalidHandle || set == IntPtr.Zero)
                return Array.Empty<DeviceBatteryInfo>();

            var data = new SP_DEVINFO_DATA { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                // Remember sibling-node classes (Keyboard / Mouse / MEDIA ...) to infer the device kind.
                Guid cid = ReadGuid(set, ref data, ContainerIdKey) ?? Guid.Empty;
                if (cid != Guid.Empty && ReadBool(set, ref data, IsConnectedKey) == true)
                    connectedContainers.Add(cid.ToString("N"));
                if (cid != Guid.Empty && ReadString(set, ref data, ClassKey) is { } cls)
                {
                    string ck = cid.ToString("N");
                    if (!classesByContainer.TryGetValue(ck, out var hs))
                        classesByContainer[ck] = hs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    hs.Add(cls);
                }

                int? battery = ReadByte(set, ref data, BatteryLife);
                if (battery is null or < 0 or > 100)
                    continue;

                // Missing property => treat as connected (node is present); false => this node is offline.
                // The battery often sits on a profile node (e.g. HFP) that stays "not connected" while the
                // device itself is connected, so the final decision uses all nodes of the container (below).
                bool connected = ReadBool(set, ref data, IsConnectedKey) ?? true;

                string instanceId = ReadInstanceId(set, ref data);
                string name = ReadString(set, ref data, FriendlyName)
                              ?? ReadString(set, ref data, BusReportedDesc)
                              ?? ReadString(set, ref data, DeviceDesc)
                              ?? "Unknown device";

                Guid container = ReadGuid(set, ref data, ContainerIdKey) ?? Guid.Empty;
                string key = container == Guid.Empty ? instanceId : container.ToString("N");

                if (connected) connectedKeys.Add(key);
                var info = new DeviceBatteryInfo(key, name, battery.Value, GuessConnection(instanceId), true);

                // Duplicate sub-nodes of the same device: keep the first one that has a real name.
                if (!byContainer.TryGetValue(key, out var existing) || existing.DeviceName == "Unknown device")
                    byContainer[key] = info;
            }
        }
        catch
        {
            // A scan failure must never take the tray app down; next tick retries.
        }
        finally
        {
            if (set != IntPtr.Zero && set != InvalidHandle)
                SetupDiDestroyDeviceInfoList(set);
        }

        return byContainer.Values
            .Where(d => connectedKeys.Contains(d.Id) || connectedContainers.Contains(d.Id))
            .Select(d => d with { Kind = ResolveKind(d, classesByContainer) })
            .OrderBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DeviceKind ResolveKind(DeviceBatteryInfo d, Dictionary<string, HashSet<string>> classes)
    {
        if (classes.TryGetValue(d.Id, out var set))
        {
            if (set.Contains("Keyboard")) return DeviceKind.Keyboard;
            if (set.Contains("Mouse")) return DeviceKind.Mouse;
            if (set.Contains("MEDIA") || set.Contains("AudioEndpoint")) return DeviceKind.Headphones;
            if (set.Contains("XboxComposite") || set.Contains("XnaComposite")) return DeviceKind.Controller;
        }
        return DeviceKindGuesser.FromName(d.DeviceName);
    }

    private static ConnectionType GuessConnection(string instanceId)
    {
        if (instanceId.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) return ConnectionType.Bluetooth;
        if (instanceId.StartsWith("USB", StringComparison.OrdinalIgnoreCase)) return ConnectionType.Usb;
        return ConnectionType.Other;
    }

    private static byte[]? ReadRaw(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key, out uint type)
    {
        type = 0;
        SetupDiGetDevicePropertyW(set, ref data, ref key, out type, null, 0, out uint required, 0);
        if (required == 0 || required > 4096)
            return null;
        var buf = new byte[required];
        return SetupDiGetDevicePropertyW(set, ref data, ref key, out type, buf, required, out _, 0) ? buf : null;
    }

    private static int? ReadByte(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
    {
        var raw = ReadRaw(set, ref data, key, out _);
        return raw is { Length: > 0 } ? raw[0] : null;
    }

    private static bool? ReadBool(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
    {
        var raw = ReadRaw(set, ref data, key, out _);
        return raw is { Length: > 0 } ? raw[0] != 0 : null;
    }

    private static Guid? ReadGuid(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
    {
        var raw = ReadRaw(set, ref data, key, out _);
        return raw is { Length: 16 } ? new Guid(raw) : null;
    }

    private static string? ReadString(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
    {
        var raw = ReadRaw(set, ref data, key, out _);
        if (raw is null || raw.Length < 2) return null;
        string s = Encoding.Unicode.GetString(raw).TrimEnd('\0').Trim();
        return s.Length == 0 ? null : s;
    }

    private static string ReadInstanceId(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        var sb = new StringBuilder(260);
        return SetupDiGetDeviceInstanceIdW(set, ref data, sb, (uint)sb.Capacity, out _) ? sb.ToString() : string.Empty;
    }
}
