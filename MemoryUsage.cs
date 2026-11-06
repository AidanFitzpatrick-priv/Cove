using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Cove;

static class MemoryUsage
{
    public static long WorkingSet()
    {
        try
        {
            var self = Environment.ProcessId;
            var ids = new List<int> { self };
            var children = ChildMap();
            var stack = new Stack<int>();
            stack.Push(self);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!children.TryGetValue(id, out var kids)) continue;
                foreach (var kid in kids)
                {
                    ids.Add(kid);
                    stack.Push(kid);
                }
            }

            long total = 0;
            foreach (var id in ids)
            {
                try
                {
                    using var process = Process.GetProcessById(id);
                    total += process.WorkingSet64;
                }
                catch
                {
                    // Process exited between the snapshot and the read.
                }
            }
            return total;
        }
        catch
        {
            return Process.GetCurrentProcess().WorkingSet64;
        }
    }

    public static string CommandLine(int processId)
    {
        var handle = OpenProcess(0x0410, false, processId);
        if (handle == IntPtr.Zero) return "";
        try
        {
            var info = new PROCESS_BASIC_INFORMATION();
            if (NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) != 0)
                return "";
            var parameters = ReadPointer(handle, info.PebBaseAddress + 0x20);
            if (parameters == IntPtr.Zero) return "";
            var header = new byte[16];
            if (!ReadBytes(handle, parameters + 0x70, header)) return "";
            var length = BitConverter.ToUInt16(header, 0);
            var buffer = new IntPtr(BitConverter.ToInt64(header, 8));
            if (length == 0 || length > 32_000 || buffer == IntPtr.Zero) return "";
            var text = new byte[length];
            return ReadBytes(handle, buffer, text) ? Encoding.Unicode.GetString(text) : "";
        }
        catch
        {
            return "";
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    static IntPtr ReadPointer(IntPtr process, IntPtr address)
    {
        var data = new byte[8];
        return ReadBytes(process, address, data) ? new IntPtr(BitConverter.ToInt64(data, 0)) : IntPtr.Zero;
    }

    static bool ReadBytes(IntPtr process, IntPtr address, byte[] buffer) =>
        ReadProcessMemory(process, address, buffer, buffer.Length, out _);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref PROCESS_BASIC_INFORMATION info, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr read);

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }

    static Dictionary<int, List<int>> ChildMap()
    {
        var map = new Dictionary<int, List<int>>();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == Invalid) return map;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry)) return map;
            do
            {
                var parent = (int)entry.th32ParentProcessID;
                var id = (int)entry.th32ProcessID;
                if (!map.TryGetValue(parent, out var list))
                {
                    list = [];
                    map[parent] = list;
                }
                list.Add(id);
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return map;
    }

    static readonly IntPtr Invalid = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }
}
