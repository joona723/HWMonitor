using System.Net;
using System.Runtime.InteropServices;

namespace HWMonitor;

sealed record ConnectionInfo(string Protocol, string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort, string State, int Pid);

/// <summary>Lists active TCP/UDP connections with their owning process, via the IP Helper API.</summary>
sealed class NetworkConnectionsService
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int ErrorInsufficientBuffer = 122;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;

    public List<ConnectionInfo> List()
    {
        var results = new List<ConnectionInfo>();
        results.AddRange(ListTcp(AfInet));
        results.AddRange(ListTcp(AfInet6));
        results.AddRange(ListUdp(AfInet));
        results.AddRange(ListUdp(AfInet6));
        return results;
    }

    public static Dictionary<int, int> CountsByPid(IEnumerable<ConnectionInfo> connections)
    {
        var counts = new Dictionary<int, int>();
        foreach (ConnectionInfo c in connections)
        {
            counts[c.Pid] = counts.GetValueOrDefault(c.Pid) + 1;
        }
        return counts;
    }

    private static IEnumerable<ConnectionInfo> ListTcp(int addressFamily)
    {
        nint buffer = 0;
        try
        {
            buffer = AllocateTable((nint buf, ref int sz) =>
                NativeMethods.GetExtendedTcpTable(buf, ref sz, false, addressFamily, TcpTableOwnerPidAll, 0));

            if (buffer == 0)
            {
                yield break;
            }

            int numEntries = Marshal.ReadInt32(buffer);
            nint rowPtr = buffer + 4;

            if (addressFamily == AfInet)
            {
                int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                for (int i = 0; i < numEntries; i++)
                {
                    var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr + i * rowSize);
                    yield return new ConnectionInfo(
                        "TCP",
                        new IPAddress(row.localAddr).ToString(),
                        SwapPort(row.localPort),
                        new IPAddress(row.remoteAddr).ToString(),
                        SwapPort(row.remotePort),
                        TcpStateName(row.state),
                        (int)row.owningPid);
                }
            }
            else
            {
                int rowSize = Marshal.SizeOf<MibTcp6RowOwnerPid>();
                for (int i = 0; i < numEntries; i++)
                {
                    var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(rowPtr + i * rowSize);
                    yield return new ConnectionInfo(
                        "TCP6",
                        new IPAddress(row.localAddr).ToString(),
                        SwapPort(row.localPort),
                        new IPAddress(row.remoteAddr).ToString(),
                        SwapPort(row.remotePort),
                        TcpStateName(row.state),
                        (int)row.owningPid);
                }
            }
        }
        finally
        {
            if (buffer != 0)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static IEnumerable<ConnectionInfo> ListUdp(int addressFamily)
    {
        nint buffer = 0;
        try
        {
            buffer = AllocateTable((nint buf, ref int sz) =>
                NativeMethods.GetExtendedUdpTable(buf, ref sz, false, addressFamily, UdpTableOwnerPid, 0));

            if (buffer == 0)
            {
                yield break;
            }

            int numEntries = Marshal.ReadInt32(buffer);
            nint rowPtr = buffer + 4;

            if (addressFamily == AfInet)
            {
                int rowSize = Marshal.SizeOf<MibUdpRowOwnerPid>();
                for (int i = 0; i < numEntries; i++)
                {
                    var row = Marshal.PtrToStructure<MibUdpRowOwnerPid>(rowPtr + i * rowSize);
                    yield return new ConnectionInfo(
                        "UDP",
                        new IPAddress(row.localAddr).ToString(),
                        SwapPort(row.localPort),
                        "*",
                        0,
                        "-",
                        (int)row.owningPid);
                }
            }
            else
            {
                int rowSize = Marshal.SizeOf<MibUdp6RowOwnerPid>();
                for (int i = 0; i < numEntries; i++)
                {
                    var row = Marshal.PtrToStructure<MibUdp6RowOwnerPid>(rowPtr + i * rowSize);
                    yield return new ConnectionInfo(
                        "UDP6",
                        new IPAddress(row.localAddr).ToString(),
                        SwapPort(row.localPort),
                        "*",
                        0,
                        "-",
                        (int)row.owningPid);
                }
            }
        }
        finally
        {
            if (buffer != 0)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private delegate uint GetTableFunc(nint buffer, ref int size);

    private static nint AllocateTable(GetTableFunc getTable)
    {
        int size = 0;
        uint result = getTable(0, ref size);
        if (result != ErrorInsufficientBuffer)
        {
            return 0;
        }

        nint buffer = Marshal.AllocHGlobal(size);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            result = getTable(buffer, ref size);
            if (result == 0)
            {
                return buffer;
            }

            if (result == ErrorInsufficientBuffer)
            {
                Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(size);
                continue;
            }

            break;
        }

        Marshal.FreeHGlobal(buffer);
        return 0;
    }

    private static int SwapPort(uint rawPort) => (int)(((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF));

    private static string TcpStateName(uint state) => state switch
    {
        1 => "CLOSED",
        2 => "LISTEN",
        3 => "SYN_SENT",
        4 => "SYN_RCVD",
        5 => "ESTABLISHED",
        6 => "FIN_WAIT1",
        7 => "FIN_WAIT2",
        8 => "CLOSE_WAIT",
        9 => "CLOSING",
        10 => "LAST_ACK",
        11 => "TIME_WAIT",
        12 => "DELETE_TCB",
        _ => "UNKNOWN",
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint state;
        public uint localAddr;
        public uint localPort;
        public uint remoteAddr;
        public uint remotePort;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] localAddr;
        public uint localScopeId;
        public uint localPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] remoteAddr;
        public uint remoteScopeId;
        public uint remotePort;
        public uint state;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdpRowOwnerPid
    {
        public uint localAddr;
        public uint localPort;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] localAddr;
        public uint localScopeId;
        public uint localPort;
        public uint owningPid;
    }

    private static class NativeMethods
    {
        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint GetExtendedTcpTable(nint pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint GetExtendedUdpTable(nint pUdpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);
    }
}
