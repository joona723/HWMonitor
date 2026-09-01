using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace HWMonitor;

readonly record struct ProcessDetails(
    int Pid,
    string Name,
    int ParentPid,
    string? Path,
    string? CommandLine,
    string? Publisher,
    string? IntegrityLevel,
    string? UserName,
    DateTime? StartTime,
    IReadOnlyList<string> Modules,
    IReadOnlyList<string> Windows);

/// <summary>
/// Expensive, on-demand process introspection (signature verification, token queries, module/window
/// enumeration). Deliberately not run on every list refresh - only when the user asks for details on
/// one specific process.
/// </summary>
static class ProcessInspector
{
    private static readonly Dictionary<string, string?> PublisherCache = new(StringComparer.OrdinalIgnoreCase);

    public static ProcessDetails Inspect(int pid)
    {
        string name = "";
        int parentPid = 0;
        string? path = null;
        DateTime? startTime = null;
        var modules = new List<string>();

        try
        {
            using Process process = Process.GetProcessById(pid);
            name = process.ProcessName;
            try { path = process.MainModule?.FileName; } catch { /* access denied or 32/64-bit mismatch */ }
            try { startTime = process.StartTime; } catch { /* protected process */ }
            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    modules.Add(module.ModuleName);
                }
            }
            catch { /* access denied; leave module list empty */ }
        }
        catch
        {
            // Process may have exited between the grid refresh and opening this dialog.
        }

        string? commandLine = GetCommandLine(pid);
        string? publisher = path is not null ? GetPublisher(path) : null;
        (string? integrity, string? userName) = GetIntegrityAndUser(pid);
        parentPid = GetParentPid(pid);
        List<string> windows = GetWindowTitles(pid);

        return new ProcessDetails(pid, name, parentPid, path, commandLine, publisher, integrity, userName, startTime, modules, windows);
    }

    private static int GetParentPid(int pid)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher($"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (System.Management.ManagementBaseObject obj in searcher.Get())
            {
                if (obj["ParentProcessId"] is uint ppid)
                {
                    return (int)ppid;
                }
            }
        }
        catch
        {
            // WMI unavailable; leave unknown.
        }

        return 0;
    }

    private static string? GetCommandLine(int pid)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (System.Management.ManagementBaseObject obj in searcher.Get())
            {
                return obj["CommandLine"] as string;
            }
        }
        catch
        {
            // WMI unavailable or access denied.
        }

        return null;
    }

    private static string? GetPublisher(string path)
    {
        if (PublisherCache.TryGetValue(path, out string? cached))
        {
            return cached;
        }

        string? publisher = null;
        try
        {
            using var cert = X509Certificate.CreateFromSignedFile(path);
            publisher = cert.Subject
                .Split(',')
                .Select(part => part.Trim())
                .FirstOrDefault(part => part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                ?[3..] ?? cert.Subject;
        }
        catch
        {
            // Unsigned, or signature couldn't be read.
        }

        PublisherCache[path] = publisher;
        return publisher;
    }

    private static (string? Integrity, string? UserName) GetIntegrityAndUser(int pid)
    {
        nint processHandle = 0;
        nint tokenHandle = 0;
        try
        {
            processHandle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (processHandle == 0)
            {
                return (null, null);
            }

            if (!NativeMethods.OpenProcessToken(processHandle, NativeMethods.TOKEN_QUERY, out tokenHandle))
            {
                return (null, null);
            }

            string? integrity = ReadIntegrityLevel(tokenHandle);
            string? userName = ReadTokenUser(tokenHandle);
            return (integrity, userName);
        }
        catch
        {
            return (null, null);
        }
        finally
        {
            if (tokenHandle != 0) NativeMethods.CloseHandle(tokenHandle);
            if (processHandle != 0) NativeMethods.CloseHandle(processHandle);
        }
    }

    private static string? ReadIntegrityLevel(nint tokenHandle)
    {
        int length = 0;
        NativeMethods.GetTokenInformation(tokenHandle, NativeMethods.TokenIntegrityLevel, 0, 0, out length);
        if (length == 0)
        {
            return null;
        }

        nint buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!NativeMethods.GetTokenInformation(tokenHandle, NativeMethods.TokenIntegrityLevel, buffer, length, out _))
            {
                return null;
            }

            nint sid = Marshal.ReadIntPtr(buffer);
            nint subAuthorityCountPtr = NativeMethods.GetSidSubAuthorityCount(sid);
            byte subAuthorityCount = Marshal.ReadByte(subAuthorityCountPtr);
            nint ridPtr = NativeMethods.GetSidSubAuthority(sid, (uint)(subAuthorityCount - 1));
            uint rid = (uint)Marshal.ReadInt32(ridPtr);

            return rid switch
            {
                >= 0x4000 => "System",
                >= 0x3000 => "High",
                >= 0x2000 => "Medium",
                >= 0x1000 => "Low",
                _ => "Untrusted",
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? ReadTokenUser(nint tokenHandle)
    {
        int length = 0;
        NativeMethods.GetTokenInformation(tokenHandle, NativeMethods.TokenUser, 0, 0, out length);
        if (length == 0)
        {
            return null;
        }

        nint buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!NativeMethods.GetTokenInformation(tokenHandle, NativeMethods.TokenUser, buffer, length, out _))
            {
                return null;
            }

            nint sid = Marshal.ReadIntPtr(buffer);

            var nameBuilder = new StringBuilder(256);
            var domainBuilder = new StringBuilder(256);
            int nameLen = nameBuilder.Capacity;
            int domainLen = domainBuilder.Capacity;

            if (!NativeMethods.LookupAccountSid(null, sid, nameBuilder, ref nameLen, domainBuilder, ref domainLen, out _))
            {
                return null;
            }

            return domainBuilder.Length > 0 ? $"{domainBuilder}\\{nameBuilder}" : nameBuilder.ToString();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static List<string> GetWindowTitles(int pid)
    {
        var titles = new List<string>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (windowPid != (uint)pid || !NativeMethods.IsWindowVisible(hWnd))
            {
                return true;
            }

            int length = NativeMethods.GetWindowTextLength(hWnd);
            if (length == 0)
            {
                return true;
            }

            var sb = new StringBuilder(length + 1);
            NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
            if (sb.Length > 0)
            {
                titles.Add(sb.ToString());
            }

            return true;
        }, 0);

        return titles;
    }

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    private static class NativeMethods
    {
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint TOKEN_QUERY = 0x0008;
        public const int TokenIntegrityLevel = 25;
        public const int TokenUser = 1;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(nint hObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool GetTokenInformation(nint tokenHandle, int tokenInformationClass, nint tokenInformation, int tokenInformationLength, out int returnLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern nint GetSidSubAuthorityCount(nint sid);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern nint GetSidSubAuthority(nint sid, uint subAuthority);

        [DllImport("advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool LookupAccountSid(string? systemName, nint sid, StringBuilder name, ref int nameLength, StringBuilder domainName, ref int domainNameLength, out int use);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(nint hWnd);

        [DllImport("user32.dll")]
        public static extern int GetWindowTextLength(nint hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);
    }
}
