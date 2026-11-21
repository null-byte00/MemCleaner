using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MemCleaner;

internal static class Program
{
    // SystemMemoryListInformation
    const int SystemMemoryListInformationClass = 0x50;
    const int MemoryFlushModifiedList = 3;
    const int MemoryPurgeStandbyList = 4;
    const int MemoryPurgeLowPriorityStandbyList = 5;

    const uint PROCESS_SET_QUOTA = 0x0100;
    const uint PROCESS_QUERY_INFORMATION = 0x0400;
    const uint FILE_CACHE_MAX_HARD_ENABLE = 1;

    const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
    const uint TOKEN_QUERY = 0x08;
    const uint SE_PRIVILEGE_ENABLED = 0x2;

    [DllImport("ntdll.dll")]
    static extern int NtSetSystemInformation(int infoClass, ref int command, int commandLen);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("psapi.dll", SetLastError = true)]
    static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetSystemFileCacheSize(IntPtr min, IntPtr max, uint flags);

    [DllImport("kernel32.dll")]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx s);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
        ref TokenPrivileges privileges, int length, IntPtr prev, IntPtr retLen);

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile;
        public ulong TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TokenPrivileges
    {
        public uint Count;
        public long Luid;
        public uint Attributes;
    }

    static ulong AvailableBytes()
    {
        var s = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        GlobalMemoryStatusEx(ref s);
        return s.AvailPhys;
    }

    static void EnablePrivilege(string name)
    {
        try
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle,
                    TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var tok)) return;
            if (!LookupPrivilegeValue(null, name, out var luid)) { CloseHandle(tok); return; }
            var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            AdjustTokenPrivileges(tok, false, ref tp, Marshal.SizeOf(tp), IntPtr.Zero, IntPtr.Zero);
            CloseHandle(tok);
        }
        catch { }
    }

    static int EmptyAllWorkingSets()
    {
        int done = 0;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var h = OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION, false, p.Id);
                if (h == IntPtr.Zero) continue;
                try { if (EmptyWorkingSet(h)) done++; }
                finally { CloseHandle(h); }
            }
            catch { }
            finally { p.Dispose(); }
        }
        return done;
    }

    static bool SystemListCommand(int command)
    {
        var cmd = command;
        return NtSetSystemInformation(SystemMemoryListInformationClass, ref cmd, sizeof(int)) == 0;
    }

    static double Mb(ulong b) => b / 1048576.0;
    static double Delta(ulong after, ulong before) => ((long)after - (long)before) / 1048576.0;

    static int Clean()
    {
        var before = AvailableBytes();

        var procs = EmptyAllWorkingSets();
        var afterWs = AvailableBytes();
        Console.WriteLine($"working sets : trimmed {procs} processes   ({Delta(afterWs, before):+0;-0;0} MB)");

        var okMod = SystemListCommand(MemoryFlushModifiedList);
        var okStandby = SystemListCommand(MemoryPurgeStandbyList);
        var okLow = SystemListCommand(MemoryPurgeLowPriorityStandbyList);
        var afterList = AvailableBytes();
        Console.WriteLine($"standby+mod  : {(okMod && okStandby ? "purged" : "FAILED - run as admin")}           ({Delta(afterList, afterWs):+0;-0;0} MB)");

        SetSystemFileCacheSize(new IntPtr(-1), new IntPtr(-1), FILE_CACHE_MAX_HARD_ENABLE);
        var afterFc = AvailableBytes();
        Console.WriteLine($"file cache   : flushed                  ({Delta(afterFc, afterList):+0;-0;0} MB)");

        Console.WriteLine($"---");
        Console.WriteLine($"available    : {Mb(before):F0} MB -> {Mb(afterFc):F0} MB  (freed {Delta(afterFc, before):+0;-0;0} MB)");
        return 0;
    }

    static int Main(string[] args)
    {
        foreach (var priv in new[]
        {
            "SeIncreaseQuotaPrivilege",   // SetSystemFileCacheSize
            "SeProfileSingleProcessPrivilege", // memory list commands
            "SeDebugPrivilege",           // open more processes
            "SeLockMemoryPrivilege",
        }) EnablePrivilege(priv);

        if (args.Length >= 2 && args[0] is "--watch" or "-w" &&
            int.TryParse(args[1], out var sec) && sec > 0)
        {
            Console.WriteLine($"watch mode: clean every {sec}s (Ctrl+C to quit)");
            while (true)
            {
                Clean();
                Thread.Sleep(TimeSpan.FromSeconds(sec));
                Console.WriteLine();
            }
        }

        return Clean();
    }
}
