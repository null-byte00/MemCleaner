using System.Runtime.InteropServices;

namespace MemCleaner;

/// <summary>Трей-режим: иконка + меню + автоматическая чистка по порогу.</summary>
internal static class TrayApp
{
    const uint WM_TRAYICON = Win32.WM_APP + 1;
    static IntPtr _hwnd;
    static Program.Options _opt = null!;
    static bool _paused;
    static Win32.NotifyIconData _nid;
    static readonly object _logLock = new();
    static readonly Win32.WndProc _wndProc = WndProc;   // держим делегат от GC

    public static int Run(Program.Options opt)
    {
        _opt = opt;
        Directory.CreateDirectory(Path.GetDirectoryName(_opt.LogPath)!);
        LogLine($"=== tray started (interval {opt.IntervalSec}s, threshold {opt.ThresholdPct}%) ===");

        var wc = new Win32.WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WndClassEx>(),
            lpfnWndProc = _wndProc,
            hInstance = Win32.GetModuleHandle(null),
            lpszClassName = "MemCleanerWnd",
        };
        Win32.RegisterClassEx(ref wc);
        _hwnd = Win32.CreateWindowEx(0, "MemCleanerWnd", "MemCleaner", 0,
            0, 0, 0, 0, Win32.HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        _nid = new Win32.NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<Win32.NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = Win32.LoadIcon(IntPtr.Zero, Win32.IDI_APPLICATION),
            szTip = "MemCleaner",
        };
        Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref _nid);
        UpdateTip("MemCleaner — watching");

        // первая проверка сразу, дальше по таймеру
        Win32.SetTimer(_hwnd, new IntPtr(1), 1500, IntPtr.Zero);
        Win32.SetTimer(_hwnd, new IntPtr(2), (uint)_opt.IntervalSec * 1000, IntPtr.Zero);

        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }

        Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref _nid);
        return 0;
    }

    static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        switch (msg)
        {
            case Win32.WM_TIMER:
                if (_paused) break;
                CheckAndClean();
                break;

            case WM_TRAYICON:
                if ((uint)l == Win32.WM_RBUTTONUP) ShowMenu();
                break;

            case Win32.WM_COMMAND:
                switch ((int)w & 0xFFFF)
                {
                    case 1: DoClean(); break;                 // Clean now
                    case 2:                                   // Pause/Resume
                        _paused = !_paused;
                        UpdateTip(_paused ? "MemCleaner — paused" : "MemCleaner — watching");
                        break;
                    case 3: Win32.DestroyWindow(hwnd); break; // Exit
                    case 4: OpenConsole(); break;             // Консоль
                }
                break;

            case Win32.WM_DESTROY:
                Win32.PostMessage(IntPtr.Zero, 0x12 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero);
                break;
        }
        return Win32.DefWindowProc(hwnd, msg, w, l);
    }

    static void CheckAndClean()
    {
        var (avail, total, pctFree) = Cleaner.MemoryStats();
        if (_opt.ThresholdPct > 0 && pctFree > _opt.ThresholdPct) return; // памяти хватает
        DoClean();
    }

    static void DoClean()
    {
        var freed = Cleaner.Clean();
        var mb = freed / 1048576.0;
        UpdateTip($"MemCleaner — {(mb >= 0 ? "+" : "")}{mb:F0} MB freed");
        LogLine($"clean: freed {mb:+0;-0;0} MB");
    }

    static void ShowMenu()
    {
        var menu = Win32.CreatePopupMenu();
        Win32.AppendMenu(menu, Win32.MF_STRING, (UIntPtr)4, "Консоль");
        Win32.AppendMenu(menu, Win32.MF_STRING, (UIntPtr)1, "Clean now");
        Win32.AppendMenu(menu, Win32.MF_STRING, (UIntPtr)2, _paused ? "Resume" : "Pause");
        Win32.AppendMenu(menu, Win32.MF_SEPARATOR, (UIntPtr)0, null);
        Win32.AppendMenu(menu, Win32.MF_STRING, (UIntPtr)3, "Exit");

        Win32.GetCursorPos(out var pt);
        Win32.SetForegroundWindow(_hwnd);
        var cmd = Win32.TrackPopupMenu(menu,
            Win32.TPM_RETURNCMD | Win32.TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        Win32.DestroyMenu(menu);
        if (cmd != 0)
            Win32.PostMessage(_hwnd, Win32.WM_COMMAND, (IntPtr)cmd, IntPtr.Zero);
    }

    static void OpenConsole()
    {
        try
        {
            var exe = Environment.ProcessPath
                ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!;
            // без аргументов = интерактивное меню; трей elevated → консоль тоже будет elevated
            System.Diagnostics.Process.Start(exe);
            LogLine("console opened from tray");
        }
        catch (Exception ex) { LogLine($"open console failed: {ex.Message}"); }
    }

    static void UpdateTip(string text)
    {
        if (text.Length > 127) text = text[..127];
        _nid.szTip = text;
        _nid.uFlags = Win32.NIF_TIP;
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref _nid);
    }

    static void LogLine(string line)
    {
        lock (_logLock)
        {
            try { File.AppendAllText(_opt.LogPath, $"[{DateTime.Now:HH:mm:ss}] {line}\n"); }
            catch { }
        }
    }
}
