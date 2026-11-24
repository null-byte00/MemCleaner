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
        public List<string> Exclusions = ["ClipBar", "ffmpeg", "ffprobe", "javaw", "java", "MemCleaner"];
        public string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MemCleaner", "log.txt");

        public static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MemCleaner", "settings.json");

        sealed class Persisted
        {
            public int IntervalSec { get; set; } = 60;
            public int ThresholdPct { get; set; } = 25;
            public List<string>? Exclusions { get; set; }
        }

        public static Options Load()
        {
            var o = new Options();
            try
            {
                if (!File.Exists(SettingsPath)) return o;
                var p = System.Text.Json.JsonSerializer.Deserialize<Persisted>(File.ReadAllText(SettingsPath));
                if (p is null) return o;
                if (p.IntervalSec > 0) o.IntervalSec = p.IntervalSec;
                if (p.ThresholdPct >= 0) o.ThresholdPct = p.ThresholdPct;
                if (p.Exclusions is { Count: > 0 }) o.Exclusions = p.Exclusions;
            }
            catch { }
            return o;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath, System.Text.Json.JsonSerializer.Serialize(
                    new Persisted { IntervalSec = IntervalSec, ThresholdPct = ThresholdPct, Exclusions = Exclusions },
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    static Mutex? _single;
    static string _logFile = "";

    internal static void Dbg(string msg)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {msg}";
            File.AppendAllText(_logFile, line + Environment.NewLine);
        }
        catch { }
    }

    static int Main(string[] args)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var o = Options.Load();
        _logFile = o.LogPath;
        Cleaner.SkipNames = new HashSet<string>(o.Exclusions, StringComparer.OrdinalIgnoreCase);
        try { Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!); } catch { }
        Dbg($"=== start, args=[{string.Join(' ', args)}] admin={IsAdmin()}");
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

        // всегда работаем от администратора: без прав — перезапуск через UAC
        if (!IsAdmin() && (args.Length == 0 || o.Tray || o.Watch || o.Install || o.Uninstall))
        {
            Dbg("не админ — перезапуск через runas");
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName!;
            try
            {
                Process.Start(new ProcessStartInfo(exe, string.Join(' ', args))
                { UseShellExecute = true, Verb = "runas" });
                Dbg("elevated-копия запущена, выхожу");
                return 0;
            }
            catch (Exception ex) { Dbg($"runas отменён: {ex.Message} — продолжаю без прав"); }
        }

        if (o.Install) return Install(o);
        if (o.Uninstall) return Uninstall();
        if (o.Status) return ShowStatus();

        EnablePrivileges();

        if (o.Tray)
        {
            _single = TakeMutex(@"Local\MemCleanerTray");
            if (_single is null) { Dbg("tray уже запущен и не убивается — выход"); return 0; }
            return TrayApp.Run(o);   // без консоли
        }

        // без флагов — интерактивное меню; --watch — консольный цикл
        if (!o.Watch && args.Length == 0)
        {
            _single = TakeMutex(@"Local\MemCleanerUI");
            if (_single is null)
            {
                Dbg("меню уже открыто и не убивается — выход");
                if (Win32.AttachConsole(Win32.ATTACH_PARENT_PROCESS))
                    Console.WriteLine("MemCleaner уже запущен.");
                return 0;
            }
            return RunMenu(o).GetAwaiter().GetResult();
        }

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

    // ---------- интерактивное меню (стрелки + Enter) ----------
    static async Task<int> RunMenu(Options o)
    {
        // GUI-экзешник: консоль надо создать самому. IsInputRedirected врёт для
        // GUI (хэндл NULL), поэтому смотрим тип реального хэндла stdin:
        // NULL → консоли нет вообще (аллоцируем), FILE_TYPE_CHAR → живая консоль,
        // pipe/file → stdin из пайпа, окно не нужно.
        var inH = Win32.GetStdHandle(Win32.STD_INPUT_HANDLE);
        var piped = inH != IntPtr.Zero && Win32.GetFileType(inH) != Win32.FILE_TYPE_CHAR;
        pipedConsole = piped;
        if (!piped)
        {
            if (!Win32.AttachConsole(Win32.ATTACH_PARENT_PROCESS))
                Win32.AllocConsole();
            // потоки могли закешироваться без консоли — перепривязываем на реальные хэндлы
            Console.SetIn(new StreamReader(Console.OpenStandardInput()));
            var cout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(cout);
            Console.SetError(cout);
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        }

        // фоновая проверка обновления — строка появится при следующей перерисовке
        _ = Task.Run(() => Updater.CheckAsync());

        var sel = 0;
        while (true)
        {
            var (avail, total, pctFree) = Cleaner.MemoryStats();
            var autostart = TaskInstalled();
            var admin = IsAdmin();
            var upd = Updater.Latest;

            var items = new List<(string Label, Func<Task<bool>> Act)>
            {
                ("Очистить память сейчас", () => Task.FromResult(ActClean())),
                ("Запустить в трее (фон, авто-чистка)", () => { StartTray(o); Pause(); return Task.FromResult(false); }),
                (autostart ? "Автозагрузка: ВКЛ — выключить" : "Автозагрузка: выкл — включить", () => Task.FromResult(ActAutostart(o, autostart, admin))),
                ("Настройки (порог %, интервал сек)", () => { ActSettings(o); return Task.FromResult(false); }),
                ($"Исключения процессов ({o.Exclusions.Count})", () => { ActExclusions(o); return Task.FromResult(false); }),
                ("История чисток", () => { ActHistory(); return Task.FromResult(false); }),
                (upd is not null ? $"Обновить до v{upd.ToString(3)}" : "Проверить обновление", ActUpdate),
            };
            if (!admin) items.Add(("Перезапуск от администратора (UAC)", () => { RestartAsAdmin(); return Task.FromResult(true); }));
            items.Add(("Выход", () => Task.FromResult(true)));
            sel = Math.Clamp(sel, 0, items.Count - 1);

            try { Console.Clear(); } catch { }
            Console.WriteLine();
            Console.WriteLine($"============ MemCleaner {Updater.Current.ToString(3)} ============");
            Console.WriteLine($"Свободно     : {avail / 1048576.0:F0} MB из {total / 1048576.0:F0} MB ({pctFree}%)");
            Console.WriteLine($"Автозагрузка : {(autostart ? "ВКЛ" : "выкл")}");
            Console.WriteLine($"Режим        : {(admin ? "администратор (полная чистка)" : "обычный (без standby/cached)")}");
            Console.WriteLine($"Режим трея   : каждые {o.IntervalSec}с, чистить при <= {o.ThresholdPct}% свободной");
            if (upd is not null)
                Console.WriteLine($"Обновление   : доступна v{upd.ToString(3)}!");
            Console.WriteLine("----------------------------------------");
            for (int i = 0; i < items.Count; i++)
            {
                if (i == sel)
                {
                    Console.ForegroundColor = ConsoleColor.Black;
                    Console.BackgroundColor = ConsoleColor.White;
                    Console.WriteLine($"> {items[i].Label}");
                    Console.ResetColor();
                }
                else Console.WriteLine($"  {items[i].Label}");
            }
            Console.WriteLine("----------------------------------------");
            Console.WriteLine("  стрелки ↑↓ — выбор, Enter — выполнить");

            if (piped)
            {
                // stdin из пайпа — стрелки недоступны, оставляем ввод номером
                var key = Console.ReadLine()?.Trim();
                if (key is null) return 0;
                if (int.TryParse(key, out var n) && n >= 1 && n <= items.Count)
                {
                    if (await items[n - 1].Act()) return 0;
                }
                else return 0;
                continue;
            }

            var k = Console.ReadKey(true);
            Dbg($"menu: key {k.Key}");
            switch (k.Key)
            {
                case ConsoleKey.UpArrow: sel = (sel - 1 + items.Count) % items.Count; break;
                case ConsoleKey.DownArrow: sel = (sel + 1) % items.Count; break;
                case ConsoleKey.Enter:
                    Dbg($"menu: выбор '{items[sel].Label}'");
                    if (await items[sel].Act()) return 0;
                    break;
                case ConsoleKey.Escape:
                    return 0;
            }
        }
    }

    static bool ActClean()
    {
        Cleaner.Clean(Console.WriteLine);
        Dbg("menu: чистка выполнена");
        Pause();
        return false;
    }

    static bool ActAutostart(Options o, bool installed, bool admin)
    {
        if (installed)
        {
            Console.WriteLine(Uninstall() == 0 ? "автозагрузка выключена" : "ошибка");
            Pause();
            return false;
        }
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
        return false;
    }

    static void ActSettings(Options o)
    {
        Console.Write($"Порог свободной памяти % [{o.ThresholdPct}]: ");
        var t = Console.ReadLine()?.Trim();
        if (int.TryParse(t, out var tv)) o.ThresholdPct = tv;
        Console.Write($"Интервал сек [{o.IntervalSec}]: ");
        var iv = Console.ReadLine()?.Trim();
        if (int.TryParse(iv, out var ivv)) o.IntervalSec = ivv;
        o.Save();
        Console.WriteLine("сохранено в settings.json");
        Pause();
    }

    static void ActExclusions(Options o)
    {
        while (true)
        {
            try { Console.Clear(); } catch { }
            Console.WriteLine("Исключения — этим процессам память не режем:");
            for (int i = 0; i < o.Exclusions.Count; i++)
                Console.WriteLine($"  {i + 1,2}. {o.Exclusions[i]}");
            Console.WriteLine();
            Console.WriteLine("  +имя — добавить   ·   -N — убрать   ·   Enter — назад");
            Console.Write("> ");
            var s = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(s)) { o.Save(); Cleaner.SkipNames = new HashSet<string>(o.Exclusions, StringComparer.OrdinalIgnoreCase); return; }
            if (s.StartsWith('+') && s.Length > 1)
            {
                var name = s[1..].Trim();
                if (!o.Exclusions.Contains(name, StringComparer.OrdinalIgnoreCase)) { o.Exclusions.Add(name); Dbg($"exclusions: +{name}"); }
            }
            else if (s.StartsWith('-') && int.TryParse(s[1..], out var n) && n >= 1 && n <= o.Exclusions.Count)
            {
                Dbg($"exclusions: -{o.Exclusions[n - 1]}");
                o.Exclusions.RemoveAt(n - 1);
            }
            else if (int.TryParse(s, out var d) && d >= 1 && d <= o.Exclusions.Count)
            {
                Dbg($"exclusions: -{o.Exclusions[d - 1]}");
                o.Exclusions.RemoveAt(d - 1);
            }
        }
    }

    static void ActHistory()
    {
        try { Console.Clear(); } catch { }
        Console.WriteLine("Последние чистки:");
        try
        {
            var lines = File.Exists(_logFile) ? File.ReadAllLines(_logFile) : [];
            var cleans = lines.Where(l => l.Contains("clean: freed") || l.Contains("чистка выполнена"))
                              .TakeLast(15).ToList();
            if (cleans.Count == 0) Console.WriteLine("  пока пусто");
            foreach (var l in cleans) Console.WriteLine("  " + l.Trim());
        }
        catch (Exception ex) { Console.WriteLine("  не удалось прочитать лог: " + ex.Message); }
        Pause();
    }

    static async Task<bool> ActUpdate()
    {
        Console.WriteLine("Проверяю github…");
        var v = await Updater.CheckAsync();
        if (v is null)
        {
            Console.WriteLine($"У тебя последняя версия — {Updater.Current.ToString(3)}");
            Pause();
            return false;
        }
        Console.WriteLine($"Качаю v{v.ToString(3)}…");
        if (!await Updater.DownloadAsync())
        {
            Console.WriteLine("не удалось скачать — проверь интернет");
            Pause();
            return false;
        }
        Console.WriteLine("Готово. Enter — обновить и перезапустить (Esc — позже)");
        var k = pipedConsole ? new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false) : Console.ReadKey(true);
        if (k.Key == ConsoleKey.Enter) Updater.ApplyAndExit();  // не возвращается
        return false;
    }

    static bool pipedConsole;

    // мьютекс с takeover: если старый экземпляр держит имя — убиваем его и занимаем.
    // второй раз окно просто так не блокируется (зависшие/зомби-процессы не мешают).
    static Mutex? TakeMutex(string name)
    {
        var m = new Mutex(true, name, out var fresh);
        if (fresh) return m;
        Dbg($"{name}: занят — убиваю старые экземпляры");
        m.Dispose();
        var wantTray = name.EndsWith("Tray", StringComparison.Ordinal);
        foreach (var p in Process.GetProcessesByName("MemCleaner"))
        {
            if (p.Id == Environment.ProcessId) continue;
            // убиваем только копию того же режима: трей не трогаем при открытии меню и наоборот
            var isTray = IsTrayProcess(p);
            if (isTray != wantTray) { p.Dispose(); continue; }
            try { Dbg($"kill old PID {p.Id}"); p.Kill(); }
            catch (Exception ex) { Dbg($"kill {p.Id} failed: {ex.Message}"); }
            finally { p.Dispose(); }
        }
        // даём мьютексу освободиться, потом захватываем заново
        for (int i = 0; i < 20; i++)
        {
            Thread.Sleep(200);
            m = new Mutex(true, name, out fresh);
            if (fresh) return m;
            m.Dispose();
        }
        return null;
    }

    static bool IsTrayProcess(Process p)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId={p.Id}");
            foreach (var mo in searcher.Get())
                return (mo["CommandLine"] as string)?.Contains("--tray") == true;
        }
        catch { }
        return false;
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
        var (_, code) = RunTool("schtasks", $"/query /tn \"{TaskName}\"");
        return code == 0;
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
        var cmd = $"/create /f /tn \"{TaskName}\" /tr \"\\\"{exe}\\\" {args}\" /sc onlogon /rl highest";
        Dbg($"schtasks {cmd}");
        var (output, code) = RunTool("schtasks", cmd);
        Dbg($"schtasks exit={code} out={output}");
        Console.WriteLine(output);
        return code == 0 ? 0 : 1;
    }

    static int Uninstall()
    {
        var (o, code) = RunTool("schtasks", $"/delete /f /tn \"{TaskName}\"");
        Dbg($"schtasks delete exit={code} out={o}");
        Console.WriteLine(code == 0 ? o : $"задача не найдена / ошибка: {o}");
        return code;
    }

    static int ShowStatus()
    {
        var (o, code) = RunTool("schtasks", $"/query /tn \"{TaskName}\" /fo list /v");
        Console.WriteLine(code != 0 || string.IsNullOrWhiteSpace(o) ? "not installed" : o);
        return 0;
    }

    static (string Output, int Code) RunTool(string exe, string args)
    {
        try
        {
            var enc = System.Text.Encoding.GetEncoding((int)Win32.GetOEMCP());
            var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = enc, StandardErrorEncoding = enc,
                UseShellExecute = false, CreateNoWindow = true,
            })!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return (o.Trim(), p.ExitCode);
        }
        catch (Exception ex) { return (ex.Message, -1); }
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
