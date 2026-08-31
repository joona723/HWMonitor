using System.Diagnostics;

namespace HWMonitor;

sealed record ProcessSample(int Pid, string Name, double CpuPercent, long WorkingSetBytes, long NetRxBytesPerSec, long NetTxBytesPerSec, int ConnectionCount);

/// <summary>Tracks per-process CPU% (normalized to 0-100 across all cores) and working set memory.</summary>
sealed class ProcessMonitorService
{
    private readonly int _processorCount = Environment.ProcessorCount;
    private Dictionary<int, TimeSpan> _lastCpuTimes = new();
    private DateTime _lastSampleUtc = DateTime.UtcNow;
    private bool _haveBaseline;

    public IReadOnlyList<ProcessSample> Sample(NetworkEtwMonitor? netMonitor, IReadOnlyDictionary<int, int> connectionCountsByPid)
    {
        DateTime now = DateTime.UtcNow;
        double elapsedSeconds = Math.Max((now - _lastSampleUtc).TotalSeconds, 0.001);

        var current = new Dictionary<int, TimeSpan>();
        var results = new List<ProcessSample>();

        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                int pid = process.Id;
                TimeSpan cpuTime;
                string name;
                long workingSet;
                try
                {
                    cpuTime = process.TotalProcessorTime;
                    name = process.ProcessName;
                    workingSet = process.WorkingSet64;
                }
                catch
                {
                    continue;
                }

                current[pid] = cpuTime;

                double cpuPercent = 0;
                if (_haveBaseline && _lastCpuTimes.TryGetValue(pid, out TimeSpan previous))
                {
                    double deltaMs = (cpuTime - previous).TotalMilliseconds;
                    if (deltaMs > 0)
                    {
                        cpuPercent = Math.Clamp(deltaMs / (elapsedSeconds * 1000.0 * _processorCount) * 100.0, 0, 100);
                    }
                }

                (long rx, long tx) = netMonitor?.GetRatesForProcess(pid) ?? (0, 0);
                connectionCountsByPid.TryGetValue(pid, out int connectionCount);

                results.Add(new ProcessSample(pid, name, cpuPercent, workingSet, rx, tx, connectionCount));
            }
        }

        _lastCpuTimes = current;
        _lastSampleUtc = now;
        _haveBaseline = true;

        return results;
    }
}
