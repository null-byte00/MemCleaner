using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MemCleaner;

internal static class Cleaner
{
    const int SystemMemoryListInformationClass = 0x50;
    const int MemoryFlushModifiedList = 3;
    const int MemoryPurgeStandbyList = 4;
    const int MemoryPurgeLowPriorityStandbyList = 5;

    const uint PROCESS_SET_QUOTA = 0x0100;
    const uint PROCESS_QUERY_INFORMATION = 0x0400;
    const uint FILE_CACHE_MAX_HARD_ENABLE = 1;

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
    internal static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx s);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile;
        public ulong TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    internal static (ulong avail, ulong total, int pctFree) MemoryStats()
    {
        var s = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        GlobalMemoryStatusEx(ref s);
        var pct = s.TotalPhys == 0 ? 0 : (int)(s.AvailPhys * 100 / s.TotalPhys);
        return (s.AvailPhys, s.TotalPhys, pct);
    }

    // процессы, чьи рабочие наборы не трогаем — запись/кодирование не должно фризить.
    // редактируется из меню «Исключения» (settings.json)
    internal static volatile HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
        { "ClipBar", "ffmpeg", "ffprobe", "javaw", "java", "MemCleaner" };

    static int EmptyAllWorkingSets()
    {
        int done = 0;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (SkipNames.Contains(p.ProcessName)) continue;
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

    static double Mb(long b) => b / 1048576.0;
    static long Delta(ulong after, ulong before) => (long)after - (long)before;

    /// <summary>Полная чистка. Возвращает освобождённые байты (может быть отрицательным).</summary>
    internal static long Clean(Action<string>? log = null)
    {
        var before = Cleaner.MemoryStats().avail;

        var procs = EmptyAllWorkingSets();
        var afterWs = Cleaner.MemoryStats().avail;
        log?.Invoke($"working sets : trimmed {procs} processes   ({Mb(Delta(afterWs, before)):+0;-0;0} MB)");

        var okMod = SystemListCommand(MemoryFlushModifiedList);
        var okStandby = SystemListCommand(MemoryPurgeStandbyList);
        var okLow = SystemListCommand(MemoryPurgeLowPriorityStandbyList);
        var afterList = Cleaner.MemoryStats().avail;
        log?.Invoke($"standby+mod  : {(okMod && okStandby ? "purged" : "no privilege")}           ({Mb(Delta(afterList, afterWs)):+0;-0;0} MB)");

        SetSystemFileCacheSize(new IntPtr(-1), new IntPtr(-1), FILE_CACHE_MAX_HARD_ENABLE);
        var afterFc = Cleaner.MemoryStats().avail;
        log?.Invoke($"file cache   : flushed                  ({Mb(Delta(afterFc, afterList)):+0;-0;0} MB)");

        var freed = Delta(afterFc, before);
        log?.Invoke($"available    : {Mb((long)before):F0} MB -> {Mb((long)afterFc):F0} MB  (freed {Mb(freed):+0;-0;0} MB)");
        return freed;
    }
}
