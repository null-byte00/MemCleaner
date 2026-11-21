using System.Runtime.InteropServices;

namespace MemCleaner;

internal static class Win32
{
    public const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AllocConsole();

    // ---- privileges ----
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
        ref TokenPrivileges privileges, int length, IntPtr prev, IntPtr retLen);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    struct TokenPrivileges { public uint Count; public long Luid; public uint Attributes; }

    public static void EnablePrivilege(string name)
    {
        try
        {
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            if (!OpenProcessToken(proc.Handle, 0x20 | 0x8, out var tok)) return;
            if (!LookupPrivilegeValue(null, name, out var luid)) { CloseHandle(tok); return; }
            var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 0x2 };
            AdjustTokenPrivileges(tok, false, ref tp, Marshal.SizeOf(tp), IntPtr.Zero, IntPtr.Zero);
            CloseHandle(tok);
        }
        catch { }
    }

    // ---- window/message loop ----
    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WndClassEx
    {
        public uint cbSize; public uint style; public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string lpszMenuName, lpszClassName; public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WndClassEx wc);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name,
        uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);

    [DllImport("user32.dll")]
    public static extern bool GetMessage(out Msg msg, IntPtr h, uint min, uint max);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref Msg msg);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr h);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint elapse, IntPtr cb);

    [DllImport("user32.dll")]
    public static extern bool KillTimer(IntPtr h, IntPtr id);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out Point pt);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr h);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadIcon(IntPtr inst, IntPtr name);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetModuleHandle(string? name);

    [StructLayout(LayoutKind.Sequential)]
    public struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam, lParam;
        public uint time; public Point pt;
    }

    public static readonly IntPtr HWND_MESSAGE = new(-3);
    public static readonly IntPtr IDI_APPLICATION = new(32512);

    // ---- tray ----
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData data);

    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    public const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NotifyIconData
    {
        public uint cbSize; public IntPtr hWnd; public uint uID;
        public uint uFlags; public uint uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    // ---- popup menu ----
    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool AppendMenu(IntPtr hMenu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenu(IntPtr h, uint flags, int x, int y,
        int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(IntPtr h);

    public const uint MF_STRING = 0, MF_SEPARATOR = 0x800, MF_CHECKED = 8;
    public const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 2;

    public const uint WM_TIMER = 0x113, WM_COMMAND = 0x111, WM_DESTROY = 0x2,
        WM_RBUTTONUP = 0x205, WM_APP = 0x8000;
}
