using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics.Eventing.Reader;

class MonitorSettings
{
    public string ExecutableName { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public int CheckIntervalSeconds { get; set; } = 30;
}

class Program
{
    static Process? _lastStartedProcess;

    static async Task Main()
    {
        // Загрузка конфигурации из appsettings.json
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var settings = config.GetSection("MonitorSettings").Get<MonitorSettings>();

        if (settings == null || string.IsNullOrWhiteSpace(settings.ExecutablePath))
        {
            Console.WriteLine("❌ Ошибка конфигурации: не указан путь к файлу.");
            return;
        }

        var interval = TimeSpan.FromSeconds(settings.CheckIntervalSeconds);

        Console.WriteLine($"[Monitor] Watching: {settings.ExecutableName} from {settings.ExecutablePath}");
        Console.WriteLine($"Check interval: {interval.TotalSeconds} seconds");

        //PrintCrashReports(settings.ExecutableName);
        // PrintFatalCrashReports(settings.ExecutableName, TimeSpan.FromMinutes(96));

        while (true)
        {
            try
            {
                Process? existingProcess = GetProcessFromPath(settings.ExecutableName, settings.ExecutablePath);

                if (existingProcess != null && !existingProcess.HasExited)
                {
                    Log("✅ Process is running.");
                }
                else
                {
                    if (existingProcess != null && existingProcess.HasExited)
                    {
                        Log($"⚠️ Процесс завершён. Код выхода: {existingProcess.ExitCode}");
                    }

                    Log("⚠ Process is NOT running. Trying to start...");

                    // Запускаем задачу, которая запускает процесс и отслеживает диалог
                    await StartProcessAndHandleDialogAsync(settings.ExecutablePath);
                }
            }
            catch (Exception ex)
            {
                Log($"❗ Error: {ex.Message}");
            }

            Thread.Sleep(interval);
        }
    }

    static async Task StartProcessAndHandleDialogAsync(string path)
    {
        var task = Task.Run(() =>
        {
            _lastStartedProcess = TryStartProcessAndReturn(path);
            if (_lastStartedProcess != null)
            {
                _lastStartedProcess.EnableRaisingEvents = true;
                _lastStartedProcess.Exited += (s, e) =>
                {
                    if (_lastStartedProcess != null)
                    {
                        Log($"⚠️ Отслеживаемый процесс завершился. Код выхода: {_lastStartedProcess?.ExitCode} — {InterpretExitCode(_lastStartedProcess!.ExitCode)}");
                    }
                    _lastStartedProcess = null;
                };
            }
        });

        var timeout = TimeSpan.FromSeconds(15);
        var sw = Stopwatch.StartNew();

        while (!task.IsCompleted && sw.Elapsed < timeout)
        {
            if (DialogHelper.TryClickRunAnywayButton())
            {
                Log("✅ Нажали кнопку 'Запустить' в диалоге безопасности.");
            }
            await Task.Delay(500);
        }

        await task;

        if (_lastStartedProcess != null && !_lastStartedProcess.HasExited)
            Log("✅ Process started.");
        else
            Log("❌ Failed to start the process.");
    }

    static Process? TryStartProcessAndReturn(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = Path.GetDirectoryName(path)!,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };

            return Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log($"Failed to start process: {ex.Message}");
            return null;
        }
    }

    static Process? GetProcessFromPath(string processName, string fullPath)
    {
        var processes = Process.GetProcessesByName(processName);
        foreach (var proc in processes)
        {
            try
            {
                if (proc.MainModule?.FileName?.Equals(fullPath, StringComparison.OrdinalIgnoreCase) == true)
                {
                    return proc;
                }
            }
            catch { }
        }
        return null;
    }

    static void PrintFatalCrashReports(string exeName, TimeSpan lookback)
    {
        const string logName = "Application";
        var lookbackMs = (long)lookback.TotalMilliseconds;

        string queryXml = $@"
<QueryList>
  <Query Id='0' Path='{logName}'>
    <Select Path='{logName}'>
      *[System[(EventID=1000) and TimeCreated[timediff(@SystemTime) &lt;= {lookbackMs}]]]
    </Select>
  </Query>
</QueryList>";

        try
        {
            var reader = new EventLogReader(new EventLogQuery(logName, PathType.LogName, queryXml));
            Log($"📋 Ищем фатальные сбои {exeName} за последние {lookback.TotalMinutes} минут...");

            int found = 0;
            EventRecord? evt;
            while ((evt = reader.ReadEvent()) != null)
            {
                using (evt)
                {
                    string? msg = evt.FormatDescription();

                    if (!string.IsNullOrEmpty(msg) && msg.Contains(exeName, StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"🛑 Краш найден:");
                        Log($"    Дата: {evt.TimeCreated}");
                        Log($"    Источник: {evt.ProviderName}, ID: {evt.Id}");
                        Log($"    {SummarizeCrashMessage(msg)}");
                        found++;
                    }
                }
            }

            if (found == 0)
            {
                Log("✅ Фатальных сбоев не найдено.");
            }
        }
        catch (Exception ex)
        {
            Log($"❗ Ошибка при чтении журнала: {ex.Message}");
        }
    }

    static string SummarizeCrashMessage(string message)
    {
        // Возвращаем первую строку и код ошибки (если есть)
        var lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var summary = lines.FirstOrDefault()?.Trim() ?? "";
        var code = lines.FirstOrDefault(l => l.Contains("исключение") || l.Contains("exception"))?.Trim();

        return summary + (code != null ? $" ({code})" : "");
    }


    static void PrintCrashReports(string exeName)
    {
        string logName = "Application";
        var query = $@"
<QueryList>
  <Query Id='0' Path='{logName}'>
    <Select Path='{logName}'>
      *[System[(EventID=1000 or EventID=1001)]]
    </Select>
  </Query>
</QueryList>";

        try
        {
            var reader = new EventLogReader(new EventLogQuery(logName, PathType.LogName, query));

            Log($"📋 Поиск краш-событий для {exeName} в журнале Windows...");
            int found = 0;

            EventRecord? evt;
            while ((evt = reader.ReadEvent()) != null)
            {
                using (evt)
                {
                    string? msg = evt.FormatDescription();

                    if (!string.IsNullOrEmpty(msg) && msg.IndexOf(exeName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Log($"🛑 Краш найден:");
                        Log($"    Дата: {evt.TimeCreated}");
                        Log($"    ID: {evt.Id}, Источник: {evt.ProviderName}");
                        Log($"    Описание: {msg.Split('\n').FirstOrDefault()?.Trim()}...");
                        found++;
                    }
                }
            }

            if (found == 0)
            {
                Log("✅ Нет записей о сбоях для указанного EXE.");
            }
        }
        catch (Exception ex)
        {
            Log($"❗ Ошибка при чтении журнала: {ex.Message}");
        }
    }

    static string InterpretExitCode(int code)
    {
        return code switch
        {
            0 => "Процесс завершился успешно.",
            unchecked((int)0xC000013A) => "Процесс завершён пользователем (Ctrl+C или закрытие окна).",
            unchecked((int)0xC0000005) => "Ошибка доступа в памяти (возможно, сбой или crash).",
            unchecked((int)0xC0000409) => "Переполнение буфера или защита от stack overflow.",
            unchecked((int)0xC0000374) => "Повреждение памяти/heap.",
            _ => $"Неизвестный код (0x{code:X8})."
        };
    }

    static void Log(string message)
    {
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
    }
}


class DialogHelper
{
    const int BM_CLICK = 0x00F5;

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public static bool TryClickRunAnywayButton()
    {
        // Попытка найти окно диалога
        // Название окна может быть разное, например:
        // "Безопасность Windows", "Windows Security", "Open File - Security Warning", "Подтверждение безопасности"
        // Можно подставить ключевые слова

        string[] possibleWindowTitles = new[]
        {
            "Безопасность Windows",
            "Windows Security",
            "Open File - Security Warning",
            "Подтверждение безопасности",
            "Безопасный запуск",
            "Открыть файл - предупреждение системы безопасности"
        };

        foreach (var title in possibleWindowTitles)
        {
            var hwnd = FindWindow(null, title);
            if (hwnd != IntPtr.Zero)
            {
                // Нашли окно, теперь ищем кнопку

                // Часто кнопка — это дочернее окно с классом "Button"
                IntPtr buttonHwnd = FindButtonByText(hwnd, new[] { "Запустить", "Run anyway", "Разрешить", "Yes" });
                if (buttonHwnd != IntPtr.Zero)
                {
                    SendMessage(buttonHwnd, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    Console.WriteLine($"[DialogHelper] Нажали кнопку в окне '{title}'");
                    return true;
                }
            }
        }

        return false;
    }

    static IntPtr FindButtonByText(IntPtr parentHwnd, string[] texts)
    {
        IntPtr found = IntPtr.Zero;

        EnumChildWindows(parentHwnd, (hwnd, lparam) =>
        {
            var sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, sb.Capacity);
            string text = sb.ToString();

            if (!string.IsNullOrEmpty(text))
            {
                foreach (var t in texts)
                {
                    if (text.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        found = hwnd;
                        return false;
                    }
                }
            }
            return true;
        }, IntPtr.Zero);

        return found;
    }
}