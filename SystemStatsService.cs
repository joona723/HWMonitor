using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace HWMonitor;

readonly record struct SystemStats(double CpuPercent, double MemoryUsedBytes, double MemoryTotalBytes, double NetworkRxBytesPerSec, double NetworkTxBytesPerSec);

/// <summary>Tracks system-wide CPU, memory, and network throughput, independent of per-process detail.</summary>
sealed class SystemStatsService
{
    private long _lastIdle;
    private long _lastKernel;
    private long _lastUser;
    private bool _haveCpuBaseline;

    private long _lastRxBytes;
    private long _lastTxBytes;
    private DateTime _lastNetSampleUtc;
    private bool _haveNetBaseline;

    public SystemStats Sample()
    {
        double cpuPercent = SampleCpu();
        (double used, double total) = SampleMemory();
        (double rx, double tx) = SampleNetwork();
        return new SystemStats(cpuPercent, used, total, rx, tx);
    }

    private double SampleCpu()
    {
        if (!NativeMethods.GetSystemTimes(out FILETIME idleFt, out FILETIME kernelFt, out FILETIME userFt))
        {
            return 0;
        }

        long idle = ToLong(idleFt);
        long kernel = ToLong(kernelFt);
        long user = ToLong(userFt);

        if (!_haveCpuBaseline)
        {
            _lastIdle = idle;
            _lastKernel = kernel;
            _lastUser = user;
            _haveCpuBaseline = true;
            return 0;
        }

        long idleDelta = idle - _lastIdle;
        long totalDelta = (kernel - _lastKernel) + (user - _lastUser);

        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;

        if (totalDelta <= 0)
        {
            return 0;
        }

        double busy = 1.0 - (double)idleDelta / totalDelta;
        return Math.Clamp(busy * 100.0, 0, 100);
    }

    private static long ToLong(FILETIME ft) => ((long)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    private static (double used, double total) SampleMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref status))
        {
            return (0, 0);
        }

        double total = status.ullTotalPhys;
        double used = total - status.ullAvailPhys;
        return (used, total);
    }

    private (double rx, double tx) SampleNetwork()
    {
        long rxTotal = 0;
        long txTotal = 0;

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            IPv4InterfaceStatistics stats;
            try
            {
                stats = nic.GetIPv4Statistics();
            }
            catch
            {
                continue;
            }

            rxTotal += stats.BytesReceived;
            txTotal += stats.BytesSent;
        }

        DateTime now = DateTime.UtcNow;
        if (!_haveNetBaseline)
        {
            _lastRxBytes = rxTotal;
            _lastTxBytes = txTotal;
            _lastNetSampleUtc = now;
            _haveNetBaseline = true;
            return (0, 0);
        }

        double elapsed = Math.Max((now - _lastNetSampleUtc).TotalSeconds, 0.001);
        double rxPerSec = Math.Max(rxTotal - _lastRxBytes, 0) / elapsed;
        double txPerSec = Math.Max(txTotal - _lastTxBytes, 0) / elapsed;

        _lastRxBytes = rxTotal;
        _lastTxBytes = txTotal;
        _lastNetSampleUtc = now;

        return (rxPerSec, txPerSec);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    }
}
