using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PerformanceTracker.Infrastructure.Services;

internal static class ProcessDetailsReader
{
    public static string? ReadWorkingDirectory(int id, DateTimeOffset start)
    {
        using SafeFileHandle process = OpenProcess(0x1010, false, (uint)id);
        if (process.IsInvalid || !GetProcessTimes(process, out long creation, out _, out _, out _) ||
            creation != start.ToFileTime() || (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess))
        {
            return null;
        }

        // A native 64-bit caller gets a second, 32-bit PEB address for a WOW64 target.
        byte[] information = new byte[6 * nint.Size];
        if (NtQueryInformationProcess(process, 26, information, nint.Size, out _) < 0)
        {
            return null;
        }

        long peb = Pointer(information, 0, !Environment.Is64BitProcess);
        bool target32Bit = peb != 0 || !Environment.Is64BitProcess;
        if (peb == 0)
        {
            if (NtQueryInformationProcess(process, 0, information, information.Length, out _) < 0)
            {
                return null;
            }

            peb = Pointer(information, nint.Size, target32Bit);
        }

        // PEB.ProcessParameters and RTL_USER_PROCESS_PARAMETERS.CurrentDirectory
        // have different offsets in the 32-bit and 64-bit layouts.
        byte[]? parameterPointer = Read(process, peb + (target32Bit ? 0x10 : 0x20), target32Bit ? 4 : 8);
        if (parameterPointer is null)
        {
            return null;
        }

        long parameters = Pointer(parameterPointer, 0, target32Bit);
        byte[]? directory = Read(process, parameters + (target32Bit ? 0x24 : 0x38), target32Bit ? 8 : 16);
        if (directory is null)
        {
            return null;
        }

        int length = BitConverter.ToUInt16(directory);
        if (length == 0 || length % 2 != 0 || length > BitConverter.ToUInt16(directory, 2))
        {
            return null;
        }

        byte[]? value = Read(process, Pointer(directory, target32Bit ? 4 : 8, target32Bit), length);
        return value is null ? null : System.Text.Encoding.Unicode.GetString(value);
    }

    private static long Pointer(byte[] data, int offset, bool target32Bit) =>
        target32Bit ? BitConverter.ToUInt32(data, offset) : BitConverter.ToInt64(data, offset);

    private static byte[]? Read(SafeFileHandle process, long address, int length)
    {
        byte[] bytes = new byte[length];
        return address > 0 && ReadProcessMemory(process, (nint)address, bytes, (nuint)length, out nuint count) &&
            count == (nuint)length ? bytes : null;
    }

    public static Dictionary<int, IReadOnlyList<string>> ReadListeningPorts()
    {
        Dictionary<int, HashSet<string>> ports = [];
        foreach (bool tcp in new[] { true, false })
        {
            foreach (int family in new[] { 2, 23 })
            {
                ReadTable(ports, tcp, family);
            }
        }

        return ports.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.Order().ToArray());
    }

    private static void ReadTable(Dictionary<int, HashSet<string>> ports, bool tcp, int family)
    {
        int size = 0;
        _ = QueryTable(0, ref size, family, tcp);
        bool ipv6 = family == 23;
        // Owner-PID rows differ by protocol and IP address size. TCP uses the listener-only table.
        int rowSize = tcp ? (ipv6 ? 56 : 24) : (ipv6 ? 28 : 12);
        for (int attempt = 0; attempt < 3 && size >= 4 && size <= 16 * 1024 * 1024; attempt++)
        {
            int capacity = size;
            nint buffer = Marshal.AllocHGlobal(capacity);
            try
            {
                uint result = QueryTable(buffer, ref size, family, tcp);
                if (result == 122)
                {
                    continue;
                }

                if (result != 0)
                {
                    return;
                }

                int count = Math.Min(Marshal.ReadInt32(buffer), (capacity - 4) / rowSize);
                for (int i = 0; i < count; i++)
                {
                    nint row = buffer + 4 + i * rowSize;
                    int offset = tcp && !ipv6 ? 8 : ipv6 ? 20 : 4;
                    int port = (Marshal.ReadByte(row, offset) << 8) | Marshal.ReadByte(row, offset + 1);
                    int pid = Marshal.ReadInt32(row, rowSize - 4);
                    if (!ports.TryGetValue(pid, out HashSet<string>? endpoints))
                    {
                        endpoints = [];
                        ports.Add(pid, endpoints);
                    }

                    bool localIpv4 = !ipv6 && (Marshal.ReadInt32(row, tcp ? 4 : 0) == 0 ||
                        Marshal.ReadByte(row, tcp ? 4 : 0) == 127);
                    endpoints.Add(tcp ? localIpv4 ? $"Local TCP {port}" : $"TCP {port}" : $"UDP {port}");
                }

                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static uint QueryTable(nint buffer, ref int size, int family, bool tcp) =>
        tcp ? GetExtendedTcpTable(buffer, ref size, false, family, 3, 0)
            : GetExtendedUdpTable(buffer, ref size, false, family, 1, 0);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(nint buffer, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, int reserved);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedUdpTable(nint buffer, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, int reserved);

    [DllImport("kernel32.dll")]
    private static extern SafeFileHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeFileHandle process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeFileHandle process, int informationClass, [Out] byte[] information, int length, out int needed);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(SafeFileHandle process, nint address, [Out] byte[] data, nuint length, out nuint count);
}
