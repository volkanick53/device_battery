using HidSharp;

namespace DeviceBattery;

/// <summary>
/// Battery reader for 2.4 GHz USB dongle devices that report battery over a vendor HID interface
/// (these never show up in Windows' PnP battery property).
///
/// Implemented:
///   * SteelSeries Arctis Nova 3P / 3X Wireless (VID 0x1038, PID 0x2269 / 0x226D), verified on hardware.
///
/// To add another device, append a <see cref="DongleDevice"/> entry below and, if the protocol differs,
/// a parser. For Logitech HID++ 2.0 (Unifying / Bolt / Lightspeed receivers, VID 0x046D) the same
/// pattern applies: open the vendor interface (usage page 0xFF00), send the HID++ "Unified Battery"
/// (0x1004) or "Battery Status" (0x1000) request and parse the reply.
/// </summary>
public sealed class HidBatteryProvider : IBatteryProvider
{
    private delegate DeviceBatteryInfo? Reader(HidDevice device, DongleDevice info);

    private sealed record DongleDevice(
        int VendorId, int ProductId, string Name, DeviceKind Kind,
        uint UsageFull, Reader Read);

    private static readonly DongleDevice[] Known =
    {
        new(0x1038, 0x2269, "Arctis Nova 3P Wireless", DeviceKind.Headphones, 0xFFC00001, ReadArctisNova),
        new(0x1038, 0x226D, "Arctis Nova 3X Wireless", DeviceKind.Headphones, 0xFFC00001, ReadArctisNova),
    };

    public IReadOnlyList<DeviceBatteryInfo> GetBatteries()
    {
        var result = new List<DeviceBatteryInfo>();
        try
        {
            foreach (var known in Known)
            {
                foreach (var hid in DeviceList.Local.GetHidDevices(known.VendorId, known.ProductId))
                {
                    if (!HasUsage(hid, known.UsageFull) || hid.GetMaxOutputReportLength() < 2)
                        continue;
                    try
                    {
                        var info = known.Read(hid, known);
                        if (info is not null)
                        {
                            result.Add(info);
                            break; // one interface is enough per product
                        }
                    }
                    catch { /* device busy / unplugged mid-read: skip this round */ }
                }
            }
        }
        catch { /* HID enumeration failure must not take the app down */ }
        return result;
    }

    private static bool HasUsage(HidDevice d, uint usage)
    {
        try
        {
            return d.GetReportDescriptor().DeviceItems
                .SelectMany(i => i.Usages.GetAllValues())
                .Any(u => u == usage);
        }
        catch { return false; }
    }

    // SteelSeries Nova protocol: write {0x00, 0xB0}; reply (after the report-id byte) is
    //   [0]=0xB0 [1]=status (0x02 = headset offline) [3]=battery % [4]=0x01 when charging.
    private static DeviceBatteryInfo? ReadArctisNova(HidDevice device, DongleDevice info)
    {
        using var stream = device.Open();
        stream.ReadTimeout = 1000;
        stream.WriteTimeout = 1000;

        var request = new byte[device.GetMaxOutputReportLength()];
        request[1] = 0xB0;
        stream.Write(request);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            var rx = new byte[device.GetMaxInputReportLength()];
            int n = stream.Read(rx);
            if (n < 6 || rx[1] != 0xB0)
                continue; // unrelated/stale input report

            string id = $"hid:{info.VendorId:x4}:{info.ProductId:x4}";
            // Headset powered off / out of range: still listed (so the menu can say so) but not connected.
            if (rx[2] == 0x02)
                return new DeviceBatteryInfo(id, info.Name, 0, ConnectionType.Usb, false, info.Kind);
            int level = rx[4];
            if (level is < 0 or > 100) return null;
            return new DeviceBatteryInfo(id, info.Name, level, ConnectionType.Usb, true, info.Kind);
        }
        return null;
    }
}
