using System.Net;
using System.Net.Sockets;

namespace HWMonitor;

/// <summary>Flags connections that match simple, low-noise heuristics for further review. Not a verdict.</summary>
static class SuspiciousConnectionAnalyzer
{
    private static readonly HashSet<int> KnownBadPorts = new()
    {
        1337, 31337, 12345, 12346, 20034, 27374, 4444, 6666, 6667, 54320, 54321, 2140, 3150, 7626, 9996,
    };

    public static string? Classify(ConnectionInfo connection, string? processImagePath)
    {
        var reasons = new List<string>();

        if (connection.RemotePort != 0 && KnownBadPorts.Contains(connection.RemotePort))
        {
            reasons.Add($"known backdoor port {connection.RemotePort}");
        }

        if (processImagePath is not null && IsInTempOrDownloads(processImagePath) && IsPublicAddress(connection.RemoteAddress))
        {
            reasons.Add("process runs from Temp/Downloads");
        }

        return reasons.Count == 0 ? null : string.Join("; ", reasons);
    }

    private static bool IsInTempOrDownloads(string path)
    {
        string lower = path.ToLowerInvariant();
        return lower.Contains("\\appdata\\local\\temp\\")
            || lower.Contains("\\windows\\temp\\")
            || lower.Contains("\\downloads\\");
    }

    private static bool IsPublicAddress(string address)
    {
        if (address == "*" || !IPAddress.TryParse(address, out IPAddress? ip))
        {
            return false;
        }

        if (IPAddress.IsLoopback(ip))
        {
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();
            if (b[0] == 10) return false;
            if (b[0] == 172 && b[1] is >= 16 and <= 31) return false;
            if (b[0] == 192 && b[1] == 168) return false;
            if (b[0] == 169 && b[1] == 254) return false;
            if (b[0] == 0) return false;
            if (b[0] >= 224) return false;
            return true;
        }

        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
        {
            return false;
        }

        byte[] b6 = ip.GetAddressBytes();
        if ((b6[0] & 0xFE) == 0xFC)
        {
            return false;
        }

        return true;
    }
}
