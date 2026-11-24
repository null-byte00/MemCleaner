using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace MemCleaner;

/// <summary>Проверка и установка обновлений с GitHub Releases.</summary>
internal static class Updater
{
    public const string Repo = "null-byte00/MemCleaner";

    public static Version Current =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    /// <summary>Результат фоновой проверки (null — не проверялось/ошибка/нет нового).</summary>
    public static volatile Version? Latest;

    static string UpdateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MemCleaner", "update");

    static string NewExePath => Path.Combine(UpdateDir, "MemCleaner.new.exe");

    public static async Task<Version?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"MemCleaner/{Current}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await http.GetAsync(
                $"https://api.github.com/repos/{Repo}/releases/latest", ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
            Latest = v > Current ? v : null;
            Program.Dbg($"update check: latest {tag}, current {Current}, hasNew={Latest != null}");
            return Latest;
        }
        catch (Exception ex)
        {
            Program.Dbg($"update check failed: {ex.Message}");
            return null;
        }
    }

    public static async Task<bool> DownloadAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"MemCleaner/{Current}");
            using var resp = await http.GetAsync(
                $"https://api.github.com/repos/{Repo}/releases/latest", ct);
            if (!resp.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            string? url = null;
            foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (name.Equals("MemCleaner.exe", StringComparison.OrdinalIgnoreCase))
                {
                    url = a.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
            if (url is null) return false;

            Directory.CreateDirectory(UpdateDir);
            var part = NewExePath + ".part";
            await using (var src = await http.GetStreamAsync(url, ct))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                await src.CopyToAsync(dst, ct);
            File.Move(part, NewExePath, overwrite: true);
            Program.Dbg($"update downloaded to {NewExePath}");
            return true;
        }
        catch (Exception ex)
        {
            Program.Dbg($"update download failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Подменяет текущий exe на скачанный и перезапускает. Процесс завершается.</summary>
    public static void ApplyAndExit()
    {
        var exe = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule!.FileName!;
        var bat = Path.Combine(UpdateDir, "apply.cmd");
        // ждём выход процесса, подменяем exe, запускаем заново, чистимся
        File.WriteAllText(bat,
            "@echo off\r\n" +
            "timeout /t 2 /nobreak >nul\r\n" +
            $"move /y \"{NewExePath}\" \"{exe}\" >nul\r\n" +
            $"start \"\" \"{exe}\"\r\n" +
            "del \"%~f0\"\r\n");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        Program.Dbg("update: applying, exiting");
        Environment.Exit(0);
    }
}
