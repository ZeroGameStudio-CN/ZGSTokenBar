using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace ZGSTokenBar.App;

internal readonly record struct NetworkCounter(string Id, long Received, long Sent);
internal readonly record struct NetworkUsage(double? Received, double? Sent, int Interfaces,
    long? ReceivedDelta = null, long? SentDelta = null);
internal sealed class NetworkUsageSampler
{
    private Dictionary<string, NetworkCounter> _previous = [];
    private double? _at;
    private NetworkInterface[] _adapters = [];
    private double _refreshAt;
    internal NetworkUsage Observe(IReadOnlyList<NetworkCounter>? counters, double seconds)
    {
        if (counters is null) { _previous.Clear(); _at = null; return new(null, null, 0); }
        var elapsed = _at is { } at ? seconds - at : 0;
        var valid = elapsed is > 0 and <= 10 && counters.Count > 0;
        long receive = 0, send = 0;
        foreach (var row in counters)
        {
            if (!_previous.TryGetValue(row.Id, out var old) || row.Received < old.Received || row.Sent < old.Sent)
                valid = false;
            else { receive += row.Received - old.Received; send += row.Sent - old.Sent; }
        }
        _previous = counters.ToDictionary(row => row.Id);
        _at = seconds;
        return new(valid ? receive / elapsed : null, valid ? send / elapsed : null, counters.Count,
            valid ? receive : null, valid ? send : null);
    }
    internal NetworkUsage Sample()
    {
        try
        {
            var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            if (now >= _refreshAt)
            {
                _adapters = NetworkInterface.GetAllNetworkInterfaces().Where(adapter =>
                    adapter.OperationalStatus == OperationalStatus.Up
                    && adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    && adapter.GetIPProperties().GatewayAddresses.Any(address => !address.Address.Equals(IPAddress.Any)
                        && !address.Address.Equals(IPAddress.IPv6Any))).ToArray();
                _refreshAt = now + 30;
            }
            var counters = new List<NetworkCounter>(_adapters.Length);
            foreach (var adapter in _adapters)
            {
                var bytes = adapter.GetIPStatistics();
                counters.Add(new(adapter.Id, bytes.BytesReceived, bytes.BytesSent));
            }
            return Observe(counters, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        }
        catch { _refreshAt = 0; return Observe(null, 0); } // Unavailable is never a measured zero.
    }
    internal static string Compact(double? value) => value is not { } number || !double.IsFinite(number) || number < 0 ? "—"
        : number >= 1024 * 1024 * 1024 ? $"{number / (1024 * 1024 * 1024):0.#}G"
        : number >= 1024 * 1024 ? $"{number / (1024 * 1024):0.#}M"
        : number >= 1024 ? $"{number / 1024:0.#}K" : $"{number:0}B";

    internal static string Total(long? bytes)
    {
        if (bytes is not >= 0) return "—";
        double value = bytes.Value;
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return $"{value:0.##} {units[index]}";
    }
}
