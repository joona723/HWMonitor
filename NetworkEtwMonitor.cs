using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace HWMonitor;

/// <summary>
/// Tracks per-process network send/receive bytes-per-second using a kernel ETW trace
/// (Microsoft-Windows-Kernel-Network). Requires the process to run elevated; the app
/// manifest already requests administrator, so this should normally succeed.
/// </summary>
sealed class NetworkEtwMonitor : IDisposable
{
    private readonly ConcurrentDictionary<int, (long Rx, long Tx)> _cumulative = new();
    private Dictionary<int, (long Rx, long Tx)> _lastCumulativeSnapshot = new();
    private Dictionary<int, (long RxPerSec, long TxPerSec)> _rates = new();
    private readonly object _rateLock = new();

    private TraceEventSession? _session;
    private Thread? _workerThread;
    private DateTime _lastRateComputeUtc;

    public bool IsRunning { get; private set; }
    public string? StartError { get; private set; }

    public bool Start()
    {
        if (IsRunning)
        {
            return true;
        }

        try
        {
            _session = new TraceEventSession(KernelTraceEventParser.KernelSessionName) { StopOnDispose = true };
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

            _session.Source.Kernel.TcpIpSend += data => Accumulate(data.ProcessID, data.size, sent: true);
            _session.Source.Kernel.TcpIpSendIPV6 += data => Accumulate(data.ProcessID, data.size, sent: true);
            _session.Source.Kernel.TcpIpRecv += data => Accumulate(data.ProcessID, data.size, sent: false);
            _session.Source.Kernel.TcpIpRecvIPV6 += data => Accumulate(data.ProcessID, data.size, sent: false);
            _session.Source.Kernel.UdpIpSend += data => Accumulate(data.ProcessID, data.size, sent: true);
            _session.Source.Kernel.UdpIpSendIPV6 += data => Accumulate(data.ProcessID, data.size, sent: true);
            _session.Source.Kernel.UdpIpRecv += data => Accumulate(data.ProcessID, data.size, sent: false);
            _session.Source.Kernel.UdpIpRecvIPV6 += data => Accumulate(data.ProcessID, data.size, sent: false);

            _lastRateComputeUtc = DateTime.UtcNow;

            _workerThread = new Thread(() =>
            {
                try
                {
                    _session.Source.Process();
                }
                catch
                {
                    // Session was stopped or the process is exiting; nothing to recover.
                }
            })
            {
                IsBackground = true,
                Name = "HWMonitor-EtwNetwork",
            };
            _workerThread.Start();

            IsRunning = true;
            StartError = null;
            return true;
        }
        catch (Exception ex)
        {
            StartError = ex.Message;
            _session?.Dispose();
            _session = null;
            IsRunning = false;
            return false;
        }
    }

    private void Accumulate(int pid, int size, bool sent)
    {
        long bytes = Math.Max(size, 0);
        _cumulative.AddOrUpdate(
            pid,
            sent ? (0, bytes) : (bytes, 0),
            (_, prev) => sent ? (prev.Rx, prev.Tx + bytes) : (prev.Rx + bytes, prev.Tx));
    }

    /// <summary>Recomputes per-process rates from accumulated byte counters. Call once per UI refresh tick.</summary>
    public void RecomputeRates()
    {
        if (!IsRunning)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        double elapsed = Math.Max((now - _lastRateComputeUtc).TotalSeconds, 0.001);
        _lastRateComputeUtc = now;

        var snapshot = new Dictionary<int, (long Rx, long Tx)>(_cumulative);
        var rates = new Dictionary<int, (long RxPerSec, long TxPerSec)>();

        foreach ((int pid, (long rx, long tx)) in snapshot)
        {
            _lastCumulativeSnapshot.TryGetValue(pid, out (long Rx, long Tx) prev);
            long rxDelta = Math.Max(rx - prev.Rx, 0);
            long txDelta = Math.Max(tx - prev.Tx, 0);
            rates[pid] = ((long)(rxDelta / elapsed), (long)(txDelta / elapsed));
        }

        _lastCumulativeSnapshot = snapshot;

        lock (_rateLock)
        {
            _rates = rates;
        }
    }

    public (long Rx, long Tx) GetRatesForProcess(int pid)
    {
        lock (_rateLock)
        {
            return _rates.TryGetValue(pid, out (long RxPerSec, long TxPerSec) rate) ? (rate.RxPerSec, rate.TxPerSec) : (0, 0);
        }
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        try
        {
            _session?.Stop();
        }
        catch
        {
            // Best-effort; Dispose below still tears down the session.
        }

        _session?.Dispose();
        _session = null;
        IsRunning = false;
        _cumulative.Clear();
        _lastCumulativeSnapshot = new Dictionary<int, (long Rx, long Tx)>();
        lock (_rateLock)
        {
            _rates = new Dictionary<int, (long RxPerSec, long TxPerSec)>();
        }
    }

    public void Dispose() => Stop();
}
