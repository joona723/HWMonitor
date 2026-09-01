using System.Diagnostics;

namespace HWMonitor;

/// <summary>
/// Reads per-process GPU engine utilization from the "GPU Engine" performance counter
/// category (populated by the WDDM graphics kernel scheduler on Windows 10/11). Sums all
/// engine instances (3D, Copy, Video Decode, etc.) per owning PID.
/// </summary>
sealed class GpuProcessUsageTracker
{
    private readonly Dictionary<string, PerformanceCounter> _counters = new();
    private bool _categoryUnavailable;

    public Dictionary<int, double> Sample()
    {
        var totals = new Dictionary<int, double>();
        if (_categoryUnavailable)
        {
            return totals;
        }

        string[] instanceNames;
        try
        {
            if (!PerformanceCounterCategory.Exists("GPU Engine"))
            {
                _categoryUnavailable = true;
                return totals;
            }

            instanceNames = new PerformanceCounterCategory("GPU Engine").GetInstanceNames();
        }
        catch
        {
            _categoryUnavailable = true;
            return totals;
        }

        var seen = new HashSet<string>(instanceNames);

        foreach (string instanceName in instanceNames)
        {
            int pid = ExtractPid(instanceName);
            if (pid <= 0)
            {
                continue;
            }

            if (!_counters.TryGetValue(instanceName, out PerformanceCounter? counter))
            {
                try
                {
                    counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instanceName, readOnly: true);
                    _counters[instanceName] = counter;
                }
                catch
                {
                    continue;
                }
            }

            float value;
            try
            {
                value = counter.NextValue();
            }
            catch
            {
                continue;
            }

            totals[pid] = totals.GetValueOrDefault(pid) + value;
        }

        foreach (string stale in _counters.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _counters[stale].Dispose();
            _counters.Remove(stale);
        }

        foreach (int pid in totals.Keys.ToList())
        {
            totals[pid] = Math.Clamp(totals[pid], 0, 100);
        }

        return totals;
    }

    private static int ExtractPid(string instanceName)
    {
        const string marker = "pid_";
        int idx = instanceName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return -1;
        }

        int start = idx + marker.Length;
        int end = start;
        while (end < instanceName.Length && char.IsDigit(instanceName[end]))
        {
            end++;
        }

        return end > start && int.TryParse(instanceName.AsSpan(start, end - start), out int pid) ? pid : -1;
    }
}
