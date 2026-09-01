using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HWMonitor;

sealed record ProcessSample(
    int Pid,
    string Name,
    double CpuPercent,
    long WorkingSetBytes,
    long NetRxBytesPerSec,
    long NetTxBytesPerSec,
    int ConnectionCount,
    int ParentPid = 0,
    long PrivateBytes = 0,
    long DiskReadBytesPerSec = 0,
    long DiskWriteBytesPerSec = 0,
    int ThreadCount = 0,
    int HandleCount = 0,
    double? GpuPercent = null);

/// <summary>Tracks per-process CPU%, memory, disk I/O, GPU engine usage, and process-tree relationships.</summary>
sealed class ProcessMonitorService
{
    private readonly int _processorCount = Environment.ProcessorCount;
    private readonly GpuProcessUsageTracker _gpuTracker = new();

    private Dictionary<int, TimeSpan> _lastCpuTimes = new();
    private Dictionary<int, (ulong Read, ulong Write)> _lastIoBytes = new();
    private DateTime _lastSampleUtc = DateTime.UtcNow;
    private bool _haveBaseline;

    public IReadOnlyList<ProcessSample> Sample(NetworkEtwMonitor? netMonitor, IReadOnlyDictionary<int, int> connectionCountsByPid)
    {
        DateTime now = DateTime.UtcNow;
        double elapsedSeconds = Math.Max((now - _lastSampleUtc).TotalSeconds, 0.001);

        Dictionary<int, int> parentPids = BuildParentPidMap();
        Dictionary<int, double> gpuPercentByPid = _gpuTracker.Sample();

        var currentCpu = new Dictionary<int, TimeSpan>();
        var currentIo = new Dictionary<int, (ulong Read, ulong Write)>();
        var results = new List<ProcessSample>();

        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                int pid = process.Id;
                TimeSpan cpuTime;
                string name;
                long workingSet;
                long privateBytes;
                try
                {
                    cpuTime = process.TotalProcessorTime;
                    name = process.ProcessName;
                    workingSet = process.WorkingSet64;
                    privateBytes = process.PrivateMemorySize64;
                }
                catch
                {
                    continue;
                }

                currentCpu[pid] = cpuTime;

                double cpuPercent = 0;
                if (_haveBaseline && _lastCpuTimes.TryGetValue(pid, out TimeSpan previous))
                {
                    double deltaMs = (cpuTime - previous).TotalMilliseconds;
                    if (deltaMs > 0)
                    {
                        cpuPercent = Math.Clamp(deltaMs / (elapsedSeconds * 1000.0 * _processorCount) * 100.0, 0, 100);
                    }
                }

                int threadCount = 0;
                try { threadCount = process.Threads.Count; } catch { /* protected process; leave 0 */ }

                int handleCount = 0;
                try { handleCount = process.HandleCount; } catch { /* protected process; leave 0 */ }

                (long readPerSec, long writePerSec) = SampleDiskIo(process, pid, currentIo, elapsedSeconds);

                (long rx, long tx) = netMonitor?.GetRatesForProcess(pid) ?? (0, 0);
                connectionCountsByPid.TryGetValue(pid, out int connectionCount);
                parentPids.TryGetValue(pid, out int parentPid);
                gpuPercentByPid.TryGetValue(pid, out double gpuPercent);

                results.Add(new ProcessSample(
                    pid, name, cpuPercent, workingSet, rx, tx, connectionCount,
                    ParentPid: parentPid,
                    PrivateBytes: privateBytes,
                    DiskReadBytesPerSec: readPerSec,
                    DiskWriteBytesPerSec: writePerSec,
                    ThreadCount: threadCount,
                    HandleCount: handleCount,
                    GpuPercent: gpuPercentByPid.Count == 0 ? null : gpuPercent));
            }
        }

        _lastCpuTimes = currentCpu;
        _lastIoBytes = currentIo;
        _lastSampleUtc = now;
        _haveBaseline = true;

        return results;
    }

    private (long ReadPerSec, long WritePerSec) SampleDiskIo(Process process, int pid, Dictionary<int, (ulong Read, ulong Write)> currentIo, double elapsedSeconds)
    {
        try
        {
            if (!NativeMethods.GetProcessIoCounters(process.Handle, out IO_COUNTERS counters))
            {
                return (0, 0);
            }

            currentIo[pid] = (counters.ReadTransferCount, counters.WriteTransferCount);

            if (!_haveBaseline || !_lastIoBytes.TryGetValue(pid, out (ulong Read, ulong Write) prev))
            {
                return (0, 0);
            }

            long readDelta = Math.Max((long)(counters.ReadTransferCount - prev.Read), 0);
            long writeDelta = Math.Max((long)(counters.WriteTransferCount - prev.Write), 0);
            return ((long)(readDelta / elapsedSeconds), (long)(writeDelta / elapsedSeconds));
        }
        catch
        {
            return (0, 0);
        }
    }

    private static Dictionary<int, int> BuildParentPidMap()
    {
        var map = new Dictionary<int, int>();
        nint snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snapshot == NativeMethods.InvalidHandleValue)
        {
            return map;
        }

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!NativeMethods.Process32First(snapshot, ref entry))
            {
                return map;
            }

            do
            {
                map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
            }
            while (NativeMethods.Process32Next(snapshot, ref entry));
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        return map;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    private static class NativeMethods
    {
        public const uint TH32CS_SNAPPROCESS = 0x2;
        public static readonly nint InvalidHandleValue = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessIoCounters(nint processHandle, out IO_COUNTERS ioCounters);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll")]
        public static extern bool Process32First(nint hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll")]
        public static extern bool Process32Next(nint hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(nint hObject);
    }
}
