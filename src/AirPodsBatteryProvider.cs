using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace DeviceBattery;

/// <summary>
/// AirPods / Beats battery from Apple's "proximity pairing" Bluetooth LE advertisement
/// (manufacturer data, company 0x004C, type 0x07). Windows exposes no battery property for these, so a
/// passive BLE watcher caches the latest packets and <see cref="GetBatteries"/> reports the strongest
/// recent one.
///
/// Limitations: the packet is only broadcast while the case lid is open or a pod is out/in the ear, and
/// carries no device identity, so with several AirPods nearby the one with the strongest signal wins.
/// Battery values come in 10 % steps.
/// </summary>
public sealed class AirPodsBatteryProvider : IBatteryProvider
{
    private const ushort AppleCompanyId = 0x004C;
    private const short MinRssi = -75;
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(3);

    private sealed record Sample(DateTime Seen, short Rssi, string Model, int? Left, int? Right, int? Case);

    private readonly object _gate = new();
    private readonly Dictionary<ulong, Sample> _samples = new();
    private BluetoothLEAdvertisementWatcher? _watcher;
    private ulong? _locked;
    private volatile bool _connected; // last known Windows connection state; avoids rescans for neighbours' packets

    /// <summary>Raised (from a worker thread) when AirPods are heard while none is being shown yet, so the
    /// UI can rescan right away instead of waiting for the next interval.</summary>
    public event Action? DeviceAppeared;

    public AirPodsBatteryProvider()
    {
        try
        {
            _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
            _watcher.Received += OnReceived;
            _watcher.Start();
        }
        catch (Exception ex)
        {
            Log.Error("AirPods watcher", ex);
            _watcher = null;
        }
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        try
        {
            foreach (var section in args.Advertisement.ManufacturerData)
            {
                if (section.CompanyId != AppleCompanyId) continue;
                var data = new byte[section.Data.Length];
                DataReader.FromBuffer(section.Data).ReadBytes(data);
                var sample = Parse(data, args.RawSignalStrengthInDBm);
                if (sample is null) continue;
                bool appeared;
                lock (_gate)
                {
                    _samples[args.BluetoothAddress] = sample;
                    appeared = _connected && _locked is null && sample.Rssi >= MinRssi;
                }
                if (appeared) DeviceAppeared?.Invoke();
            }
        }
        catch { /* never throw from the callback */ }
    }

    private static Sample? Parse(byte[] d, short rssi)
    {
        // 07 19 01 <model hi> <model lo> <status> <pods> <flags|case> ...
        if (d.Length < 8 || d[0] != 0x07 || d[1] < 0x0A) return null;

        int model = (d[3] << 8) | d[4];
        bool flipped = (d[5] & 0x20) == 0; // bit 1 of the status high nibble
        int podsByte = d[6];
        int hi = podsByte >> 4, lo = podsByte & 0x0F;
        int? left = Level(flipped ? hi : lo);
        int? right = Level(flipped ? lo : hi);
        int? cs = Level(d[7] & 0x0F);

        return new Sample(DateTime.UtcNow, rssi, ModelName(model), left, right, cs);
    }

    private static int? Level(int nibble) => nibble switch
    {
        <= 10 => nibble * 10,
        _ => null // 0xF = not available (pod in case / not reported)
    };

    private static string ModelName(int model) => model switch
    {
        0x0220 => "AirPods",
        0x0F20 => "AirPods 2",
        0x1320 => "AirPods 3",
        0x1920 or 0x1B20 => "AirPods 4",
        0x0E20 => "AirPods Pro",
        0x1420 or 0x2420 => "AirPods Pro 2",
        0x0A20 => "AirPods Max",
        0x0B20 => "Powerbeats Pro",
        _ => "AirPods"
    };

    public IReadOnlyList<DeviceBatteryInfo> GetBatteries()
    {
        // Only report AirPods that are paired with this PC AND currently connected. The broadcast carries
        // no identity, so without this gate any AirPods in range (a colleague's) would be picked up.
        var (_, connected) = PnpBatteryProvider.QueryNamedBluetooth("airpods", "powerbeats", "beats");
        _connected = connected;
        if (!connected)
        {
            lock (_gate) { _samples.Clear(); _locked = null; } // start fresh on the next connection
            return Array.Empty<DeviceBatteryInfo>();
        }

        Sample? best = null;
        lock (_gate)
        {
            var cutoff = DateTime.UtcNow - MaxAge;
            foreach (var key in _samples.Where(kv => kv.Value.Seen < cutoff).Select(kv => kv.Key).ToList())
                _samples.Remove(key);

            // Several AirPods can be in range (colleagues'), and nothing in the packet says whose they are.
            // Lock onto one address (the closest = strongest when first chosen) and stay on it while it keeps
            // advertising, so values don't flip between devices. Switch only if it disappears or another one
            // is clearly closer (>15 dB stronger).
            var recent = _samples.Where(kv => kv.Value.Rssi >= MinRssi).ToList();
            var strongest = recent.OrderByDescending(kv => kv.Value.Rssi).FirstOrDefault();
            if (_locked is null || !_samples.ContainsKey(_locked.Value)
                || (strongest.Value is { } st && st.Rssi - _samples[_locked.Value].Rssi > 15))
            {
                var before = _locked;
                _locked = recent.Count > 0 ? strongest.Key : null;
                if (_locked != before)
                    Log.Write($"AirPods: locked to {_locked:X} among {recent.Count} nearby (rssi {strongest.Value?.Rssi})");
            }
            best = _locked is { } k && _samples.TryGetValue(k, out var cur) ? cur : null;
        }
        if (best is null) return Array.Empty<DeviceBatteryInfo>();

        var pods = new[] { best.Left, best.Right }.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        int? level = pods.Count > 0 ? pods.Min() : best.Case;
        if (level is null) return Array.Empty<DeviceBatteryInfo>();

        string detail = string.Join(" ", new[]
        {
            best.Left is { } l ? $"L {l}%" : null,
            best.Right is { } r ? $"R {r}%" : null,
            best.Case is { } c ? $"Case {c}%" : null,
        }.Where(s => s != null));

        return new[]
        {
            new DeviceBatteryInfo("airpods", $"{best.Model} ({detail})", level.Value,
                ConnectionType.Bluetooth, true, DeviceKind.Headphones)
        };
    }
}
