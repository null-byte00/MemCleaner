using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MemCleaner;

internal static class Program
{
    // ---------- args ----------
    internal sealed class Options
    {
        public bool Tray, Watch, Install, Uninstall, Status, Help;
        public int IntervalSec = 60;
        public int ThresholdPct = 25;   // чистить только когда свободно <= N%
        public string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MemCleaner", "log.txt");
    }

    static int Main(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--tray": o.Tray = true; break;
                case "--install": o.Install = true; break;
                case "--uninstall": o.Uninstall = true; break;
                case "--status": o.Status = true; break;
                case "-h": case "--help": o.Help = true; break;
                case "--watch": case "-w": o.Watch = true; break;
                case "--interval": case "-i":
                    if (i + 1 < args.Length) int.TryParse(args[++i], out o.IntervalSec); break;
                case "--threshold": case "-t":
                    if (i + 1 < args.Length) int.TryParse(args[++i], out o.ThresholdPct); break;
            }
        }

        if (o.Help) { ShowHelp(); return 0; }
        if (o.Install) return Install(o);
        if (o.Uninstall) return Uninstall();
        if (o.Status) return ShowStatus();

        EnablePrivileges();

        if (o.Tray)
            return TrayApp.Run(o);   // без консоли

        // без флагов — интерактивное меню; --watch — консольный цикл
        if (!o.Watch && args.Length == 0)
            return RunMenu(o);

        if (o.Tray)
            return TrayApp.Run(o);   // без консоли

        // консольный режим: подцепиться к родительской консоли или создать свою
        var attached = Win32.AttachConsole(Win32.ATTACH_PARENT_PROCESS);
        var allocated = !attached && Win32.AllocConsole();
        if (attached || allocated)
        {
            Console.WriteLine("MemCleaner 0.2");
            if (o.Watch)
            {
                Console.WriteLine($"watch: check every {o.IntervalSec}s, clean when free <= {o.ThresholdPct}%  (Ctrl+C — выход)");
                Console.WriteLine();
                while (true)
                {
                    var (_, _, pctFree) = Cleaner.MemoryStats();
                    if (o.ThresholdPct == 0 || pctFree <= o.ThresholdPct)
                        Cleaner.Clean(Console.WriteLine);
                    else
                        Console.WriteLine($"{DateTime.Now:HH:mm:ss} free {pctFree}% — above threshold, skip");
                    Thread.Sleep(TimeSpan.FromSeconds(o.IntervalSec));
                    Console.WriteLine();
                }
            }
            Cleaner.Clean(Console.WriteLine);
            if (allocated)
            {
                Console.WriteLine();
                Console.Write("press any key...");
                Console.ReadKey(true);
            }
        }
        return 0;
    }

    // ---------- интерактивное меню ----------
    static int RunMenu(Options o)
    {
        if (!Console.IsOutputRedirected && !Win32.AttachConsole(Win32.ATTACH_PARENT_PROCESS))
            Win32.AllocConsole();
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        while (true)
        {
            var (avail, total, pctFree) = Cleaner.MemoryStats();
            var autostart = TaskInstalled();

            var admin = IsAdmin();
            Console.WriteLine();
            Console.WriteLine("============ MemCleaner 1.0 ============");
            Console.WriteLine($"Свободно     : {avail / 1048576.0:F0} MB из {total / 1048576.0:F0} MB ({pctFree}%)");
            Console.WriteLine($"Автозагрузка : {(autostart ? "ВКЛ" : "выкл")}");
            Console.WriteLine($"Режим        : {(admin ? "администратор (полная чистка)" : "обычный (без standby/cached)")}");
            Console.WriteLine($"Режим трея   : каждые {o.IntervalSec}с, чистить при <= {o.ThresholdPct}% свободной");
            Console.WriteLine("----------------------------------------");
            Console.WriteLine("  1 — Очистить память сейчас");
            Console.WriteLine("  2 — Запустить в трее (фон, авто-чистка)");
            Console.WriteLine("  3 — Включить автозагрузку");
            Console.WriteLine("  4 — Выключить автозагрузку");
            Console.WriteLine("  5 — Настройки (порог %, интервал сек)");
            if (!admin) Console.WriteLine("  6 — Перезапуск от администратора (UAC)");
            Console.WriteLine("  0 — Выход");
            Console.Write("> ");

            var key = Console.ReadLine()?.Trim();
            if (key is null or "0") return 0;

            switch (key)
            {
                case "1":
                    Cleaner.Clean(Console.WriteLine);
                    Pause();
                    break;
                case "2":
                    StartTray(o);
                    Pause();
                    break;
                case "3":
                    if (!admin)
                    {
                        // перезапуск себя elevated — UAC по явному выбору пользователя
                        var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName!;
                        try
                        {
                            Process.Start(new ProcessStartInfo(exe,
                                $"--install --interval {o.IntervalSec} --threshold {o.ThresholdPct}")
                            { UseShellExecute = true, Verb = "runas" });
                            Console.WriteLine("подтверди UAC — задача будет создана от администратора");
                        }
                        catch { Console.WriteLine("отменено"); }
                    }
                    else Console.WriteLine(Install(o) == 0 ? "автозагрузка включена" : "не вышло");
                    Pause();
                    break;
                case "4":
                    Console.WriteLine(Uninstall() == 0 ? "автозагрузка выключена" : "ошибка");
                    Pause();
                    break;
                case "5":
                    Console.Write($"Порог свободной памяти % [{o.ThresholdPct}]: ");
                    var t = Console.ReadLine()?.Trim();
                    if (int.TryParse(t, out var tv)) o.ThresholdPct = tv;
                    Console.Write($"Интервал сек [{o.IntervalSec}]: ");
                    var iv = Console.ReadLine()?.Trim();
                    if (int.TryParse(iv, out var ivv)) o.IntervalSec = ivv;
                    Console.WriteLine("сохранено (вступит при следующем запуске трея/установке)");
                    Pause();
                    break;
                case "6":
                    if (!admin) { RestartAsAdmin(); return 0; }
                    break;
            }
        }
    }

    static bool IsAdmin()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    static void RestartAsAdmin()
    {
        var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName!;
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" }); }
        catch { }
    }

    static void Pause()
    {
        Console.WriteLine();
        Console.Write("Enter — назад...");
        Console.ReadLine();
    }

    static bool TaskInstalled()
    {
        var r = RunTool("schtasks", $"/query /tn \"{TaskName}\" /fo list");
        return r.Contains(TaskName, StringComparison.OrdinalIgnoreCase);
    }

    static void StartTray(Options o)
    {
        var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName!;
        var args = $"--tray --interval {o.IntervalSec} --threshold {o.ThresholdPct}";
        try
        {
            // без прав — трей стартует ограниченным; с правами — полноценным (UAC по выбору юзера)
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = true };
            if (!IsAdmin()) psi.Verb = "runas";
            Process.Start(psi);
            Console.WriteLine("запущено — иконка в трее (правый клик: Clean now / Pause / Exit)");
        }
        catch (Exception ex) { Console.WriteLine($"ошибка запуска: {ex.Message}"); }
    }

    static void ShowHelp()
    {
        Console.WriteLine(
            "MemCleaner.exe                 clean once (console)\n" +
            "MemCleaner.exe --watch         clean in a loop, console\n" +
            "MemCleaner.exe --tray          tray icon + auto-clean\n" +
            "  --interval N / -i N          seconds between checks (default 60)\n" +
            "  --threshold N / -t N         clean only when free RAM <= N% (default 25; 0 = always)\n" +
            "MemCleaner.exe --install       add to autostart (Task Scheduler, elevated)\n" +
            "MemCleaner.exe --uninstall     remove from autostart\n" +
            "MemCleaner.exe --status        show autostart state");
    }

    // ---------- autostart (Task Scheduler: тихий elevated-запуск при логине) ----------
    const string TaskName = "MemCleaner";

    static int Install(Options o)
    {
        var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName!;
        var args = $"--tray --interval {o.IntervalSec} --threshold {o.ThresholdPct}";
        var r = RunTool("schtasks",
            $"/create /f /tn \"{TaskName}\" /tr \"\\\"{exe}\\\" {args}\" /sc onlogon /rl highest /delay 0000:30");
        Console.WriteLine(r);
        return r.Contains("SUCCESS") || r.Contains("успех") ? 0 : 1;
    }

    static int Uninstall()
    {
        var r = RunTool("schtasks", $"/delete /f /tn \"{TaskName}\"");
        Console.WriteLine(r);
        return 0;
    }

    static int ShowStatus()
    {
        var r = RunTool("schtasks", $"/query /tn \"{TaskName}\" /fo list /v");
        Console.WriteLine(string.IsNullOrWhiteSpace(r) ? "not installed" : r);
        return 0;
    }

    static string RunTool(string exe, string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(exe, args)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return o.Trim();
        }
        catch (Exception ex) { return ex.Message; }
    }

    // ---------- privileges ----------
    internal static void EnablePrivileges()
    {
        foreach (var priv in new[]
        {
            "SeIncreaseQuotaPrivilege",
            "SeProfileSingleProcessPrivilege",
            "SeDebugPrivilege",
            "SeLockMemoryPrivilege",
        }) Win32.EnablePrivilege(priv);
    }
}
