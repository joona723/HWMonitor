using System.Collections.Concurrent;
using System.Net;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace HWMonitor;

/// <summary>Identifies a single connection the same way <see cref="ConnectionInfo"/> does, for per-connection rate lookups.</summary>
readonly record struct ConnectionKey(int Pid, string Protocol, int LocalPort, string RemoteAddress, int RemotePort)
{
    public static ConnectionKey From(ConnectionInfo c) => new(c.Pid, c.Protocol, c.LocalPort, c.RemoteAddress, c.RemotePort);
}

/// <summary>
/// Tracks per-process and per-connection network send/receive bytes-per-second using a kernel
/// ETW trace (Microsoft-Windows-Kernel-Network). Requires the process to run elevated; the app
/// manifest already requests administrator, so this should normally succeed.
/// </summary>
sealed class NetworkEtwMonitor : IDisposable
{
    private readonly ConcurrentDictionary<int, (long Rx, long Tx)> _cumulative = new();
    private Dictionary<int, (long Rx, long Tx)> _lastCumulativeSnapshot = new();
    private Dictionary<int, (long RxPerSec, long TxPerSec)> _rates = new();
    private readonly object _rateLock = new();

    private readonly ConcurrentDictionary<ConnectionKey, (long Rx, long Tx)> _connCumulative = new();
    private Dictionary<ConnectionKey, (long Rx, long Tx)> _lastConnCumulativeSnapshot = new();
    private Dictionary<ConnectionKey, (long RxPerSec, long TxPerSec)> _connRates = new();
    private readonly object _connRateLock = new();

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

            _session.Source.Kernel.TcpIpSend += data => Accumulate(data.ProcessID, "TCP", data.size, sent: true, data.saddr, data.sport, data.daddr, data.dport);
            _session.Source.Kernel.TcpIpSendIPV6 += data => Accumulate(data.ProcessID, "TCP6", data.size, sent: true, data.saddr, data.sport, data.daddr, data.dport);
            _session.Source.Kernel.TcpIpRecv += data => Accumulate(data.ProcessID, "TCP", data.size, sent: false, data.saddr, data.sport, data.daddr, data.dport);
            _session.Source.Kernel.TcpIpRecvIPV6 += data => Accumulate(data.ProcessID, "TCP6", data.size, sent: false, data.saddr, data.sport, data.daddr, data.dport);
            _session.Source.Kernel.UdpIpSend += data => Accumulate(data.ProcessID, "UDP", data.size, sent: true, data.saddr, data.sport, data.daddr, data.dport);
            _session.Source.Kernel.UdpIpSendIPV6 += data => Accumulate(data.ProcessID, "UDP6", data.size, sent: true, data.saddr, data.sport, data.daddr, data.dport);
            _session.Source.Kernel.UdpIpRecv += data => Accumulate(data.ProcessID, "UDP", data.size, sent: false, data.saddr, data.sport, data.daddr, data.dport);
            _session.Source.Kernel.UdpIpRecvIPV6 += data => Accumulate(data.ProcessID, "UDP6", data.size, sent: false, data.saddr, data.sport, data.daddr, data.dport);

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

    private void Accumulate(int pid, string protocol, int size, bool sent, IPAddress srcAddr, int srcPort, IPAddress dstAddr, int dstPort)
    {
        long bytes = Math.Max(size, 0);
        _cumulative.AddOrUpdate(
            pid,
            sent ? (0, bytes) : (bytes, 0),
            (_, prev) => sent ? (prev.Rx, prev.Tx + bytes) : (prev.Rx + bytes, prev.Tx));

        // Packet direction (src/dst) reflects the wire, not "local vs remote"; flip it back to local/remote using
        // the send/receive flag so the key lines up with ConnectionInfo's LocalPort/RemoteAddress/RemotePort.
        (IPAddress Addr, int Port) local = sent ? (srcAddr, srcPort) : (dstAddr, dstPort);
        (IPAddress Addr, int Port) remote = sent ? (dstAddr, dstPort) : (srcAddr, srcPort);

        bool isUdp = protocol.StartsWith("UDP", StringComparison.Ordinal);
        // UDP sockets aren't "connected" - NetworkConnectionsService reports them with a wildcard remote
        // endpoint, so per-datagram traffic to any destination must roll up under that same wildcard key.
        var key = new ConnectionKey(pid, protocol, local.Port, isUdp ? "*" : remote.Addr.ToString(), isUdp ? 0 : remote.Port);
        _connCumulative.AddOrUpdate(
            key,
            sent ? (0, bytes) : (bytes, 0),
            (_, prev) => sent ? (prev.Rx, prev.Tx + bytes) : (prev.Rx + bytes, prev.Tx));
    }

    private static Dictionary<TKey, (long RxPerSec, long TxPerSec)> ComputeRates<TKey>(
        Dictionary<TKey, (long Rx, long Tx)> current,
        Dictionary<TKey, (long Rx, long Tx)> previous,
        double elapsedSeconds) where TKey : notnull
    {
        var rates = new Dictionary<TKey, (long RxPerSec, long TxPerSec)>();
        foreach ((TKey key, (long rx, long tx)) in current)
        {
            previous.TryGetValue(key, out (long Rx, long Tx) prev);
            long rxDelta = Math.Max(rx - prev.Rx, 0);
            long txDelta = Math.Max(tx - prev.Tx, 0);
            rates[key] = ((long)(rxDelta / elapsedSeconds), (long)(txDelta / elapsedSeconds));
        }
        return rates;
    }

    /// <summary>Recomputes per-process and per-connection rates from accumulated byte counters. Call once per UI refresh tick.</summary>
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
        Dictionary<int, (long RxPerSec, long TxPerSec)> rates = ComputeRates(snapshot, _lastCumulativeSnapshot, elapsed);
        _lastCumulativeSnapshot = snapshot;
        lock (_rateLock)
        {
            _rates = rates;
        }

        var connSnapshot = new Dictionary<ConnectionKey, (long Rx, long Tx)>(_connCumulative);
        Dictionary<ConnectionKey, (long RxPerSec, long TxPerSec)> connRates = ComputeRates(connSnapshot, _lastConnCumulativeSnapshot, elapsed);
        _lastConnCumulativeSnapshot = connSnapshot;
        lock (_connRateLock)
        {
            _connRates = connRates;
        }

        // Bound memory: connections churn constantly, so drop entries that went idle this tick. A connection
        // that resumes traffic later just starts accumulating from zero again, which is harmless.
        foreach ((ConnectionKey key, (long RxPerSec, long TxPerSec) rate) in connRates)
        {
            if (rate.RxPerSec == 0 && rate.TxPerSec == 0)
            {
                _connCumulative.TryRemove(key, out _);
            }
        }
    }

    public (long Rx, long Tx) GetRatesForProcess(int pid)
    {
        lock (_rateLock)
        {
            return _rates.TryGetValue(pid, out (long RxPerSec, long TxPerSec) rate) ? (rate.RxPerSec, rate.TxPerSec) : (0, 0);
        }
    }

    public (long Rx, long Tx) GetRatesForConnection(ConnectionInfo connection)
    {
        lock (_connRateLock)
        {
            return _connRates.TryGetValue(ConnectionKey.From(connection), out (long RxPerSec, long TxPerSec) rate)
                ? (rate.RxPerSec, rate.TxPerSec)
                : (0, 0);
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

        _connCumulative.Clear();
        _lastConnCumulativeSnapshot = new Dictionary<ConnectionKey, (long Rx, long Tx)>();
        lock (_connRateLock)
        {
            _connRates = new Dictionary<ConnectionKey, (long RxPerSec, long TxPerSec)>();
        }
    }

    public void Dispose() => Stop();
}
