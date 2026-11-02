using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace RobloxAccountManager.Services
{
    public static class DiagnosticsService
    {
        private const int MaxEntries = 200;
        private const int MaxMessageLength = 600;
        private const long MaxLogFileBytes = 256 * 1024; // 256 KB max on disk

        private static readonly object SyncLock = new();
        private static readonly LinkedList<string> RingBuffer = new();
        private static bool _isInitialized;
        private static int _errorCount;

        private static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RobloxAccountManager");

        private static readonly string CrashLogFilePath = Path.Combine(LogDir, "diagnostics_latest.log");

        // Регулярные выражения для гарантированной очистки любых чувствительных токенов перед записью в буфер
        private static readonly Regex CookieScrubRegex = new(
            @"(_\|WARNING:-DO-NOT-SHARE-THIS[^\s""';&]+|\.ROBLOSECURITY=[^\s""';&]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static int ErrorCount => _errorCount;

        public static void Initialize()
        {
            lock (SyncLock)
            {
                if (_isInitialized) return;
                _isInitialized = true;
            }

            try
            {
                if (!Directory.Exists(LogDir))
                    Directory.CreateDirectory(LogDir);
            }
            catch { }

            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
            LogInfo("App", $"Запуск Roblox Account Manager v{version} ({RuntimeInformation.OSDescription}, .NET {Environment.Version})");

            // 1. Ошибки UI-потока WPF
            if (Application.Current != null)
            {
                Application.Current.DispatcherUnhandledException += OnDispatcherUnhandledException;
            }

            // 2. Фатальные ошибки любых фоновых потоков AppDomain
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

            // 3. Необработанные исключения асинхронных задач (Task)
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            // 4. FirstChanceException — ловит даже скрытые/проглоченные исключения внутри try/catch,
            // но фильтрует штатный сетевой/системный шум и дедуплицирует повторы, не засоряя ОЗУ.
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        }

        private static string? _lastFirstChanceKey;
        private static DateTime _lastFirstChanceUtc = DateTime.MinValue;

        private static void OnFirstChanceException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            try
            {
                Exception ex = e.Exception;
                if (ex is OperationCanceledException or TaskCanceledException)
                    return;

                // Пропускаем внутренние системные исключения парсинга/сокет-фоллбэка, если они не относятся к нашему коду
                string? stack = ex.StackTrace;
                if (stack != null && !stack.Contains("RobloxAccountManager", StringComparison.Ordinal))
                    return;

                string key = $"{ex.GetType().Name}:{ex.Message}";
                DateTime now = DateTime.UtcNow;
                if (key == _lastFirstChanceKey && (now - _lastFirstChanceUtc).TotalSeconds < 10)
                    return;

                _lastFirstChanceKey = key;
                _lastFirstChanceUtc = now;

                LogException("HandledEx", ex, flushToDisk: false);
            }
            catch { }
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogException("UI_Unhandled", e.Exception, flushToDisk: true);
            // Не даем приложению внезапно закрыться от случайной ошибки UI
            e.Handled = true;
        }

        private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogException("Fatal_Domain", ex, flushToDisk: true);
            }
            else
            {
                AddEntry("FATAL", "Domain", $"Unhandled object: {e.ExceptionObject}", flushToDisk: true);
            }
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            LogException("AsyncTask", e.Exception, flushToDisk: true);
            e.SetObserved();
        }

        public static void LogInfo(string source, string message)
        {
            AddEntry("INFO", source, message, flushToDisk: false);
        }

        public static void LogWarning(string source, string message)
        {
            AddEntry("WARN", source, message, flushToDisk: false);
        }

        public static void LogError(string source, string message, Exception? ex = null)
        {
            System.Threading.Interlocked.Increment(ref _errorCount);
            if (ex != null)
            {
                LogException(source, ex, flushToDisk: true, customPrefix: message);
            }
            else
            {
                AddEntry("ERROR", source, message, flushToDisk: true);
            }
        }

        public static void LogException(string source, Exception ex, bool flushToDisk = true, string? customPrefix = null)
        {
            if (flushToDisk)
            {
                System.Threading.Interlocked.Increment(ref _errorCount);
            }

            Exception root = ex.GetBaseException();
            string topFrame = ExtractTopAppFrame(ex.StackTrace ?? root.StackTrace);
            string prefix = string.IsNullOrWhiteSpace(customPrefix) ? string.Empty : $"{customPrefix} — ";
            string msg = $"{prefix}{root.GetType().Name}: {root.Message}";
            if (!string.IsNullOrEmpty(topFrame))
            {
                msg += $" @ {topFrame}";
            }

            AddEntry(flushToDisk ? "ERROR" : "TRACE", source, msg, flushToDisk);
        }

        private static string ExtractTopAppFrame(string? stackTrace)
        {
            if (string.IsNullOrWhiteSpace(stackTrace))
                return string.Empty;

            string[] lines = stackTrace.Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Contains("RobloxAccountManager", StringComparison.Ordinal))
                {
                    int idx = line.IndexOf("RobloxAccountManager", StringComparison.Ordinal);
                    return line[idx..];
                }
            }

            return lines.Length > 0 ? lines[0].Trim() : string.Empty;
        }

        private static void AddEntry(string level, string source, string rawMessage, bool flushToDisk)
        {
            try
            {
                string clean = ScrubSensitive(rawMessage);
                if (clean.Length > MaxMessageLength)
                {
                    clean = clean[..MaxMessageLength] + "...";
                }

                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{source}] {clean}";

                lock (SyncLock)
                {
                    RingBuffer.AddLast(line);
                    while (RingBuffer.Count > MaxEntries)
                    {
                        RingBuffer.RemoveFirst();
                    }

                    if (flushToDisk)
                    {
                        FlushRingBufferToDiskUnsafe();
                    }
                }
            }
            catch { }
        }

        private static string ScrubSensitive(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            string singleLine = input.Replace("\r", " ").Replace("\n", " | ");
            return CookieScrubRegex.Replace(singleLine, "[REDACTED_COOKIE]");
        }

        private static void FlushRingBufferToDiskUnsafe()
        {
            try
            {
                var sb = new StringBuilder(16384);
                sb.AppendLine("=== Roblox Account Manager Compact Diagnostics ===");
                sb.AppendLine($"Saved: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine("==================================================");
                foreach (string entry in RingBuffer)
                {
                    sb.AppendLine(entry);
                }

                byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
                if (bytes.Length > MaxLogFileBytes)
                {
                    File.WriteAllBytes(CrashLogFilePath, bytes[^((int)MaxLogFileBytes)..]);
                }
                else
                {
                    File.WriteAllBytes(CrashLogFilePath, bytes);
                }
            }
            catch { }
        }

        /// <summary>
        /// Формирует компактный самодостаточный отчет для сохранения в файл (включает прошлый краш-лог, если он был, и текущий буфер).
        /// </summary>
        public static string ExportCompactReport(int configuredAccountsCount, int runningRobloxWindowsCount)
        {
            var sb = new StringBuilder(16384);
            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
            long ramMb = GC.GetTotalMemory(false) / (1024 * 1024);

            sb.AppendLine("=== ROBLOX ACCOUNT MANAGER — DIAGNOSTIC LOG ===");
            sb.AppendLine($"Time:      {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Version:   v{version}");
            sb.AppendLine($"OS:        {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
            sb.AppendLine($"Runtime:   .NET {Environment.Version} | App RAM: ~{ramMb} MB");
            sb.AppendLine($"State:     Accounts={configuredAccountsCount}, RunningRoblox={runningRobloxWindowsCount}, Errors={_errorCount}");
            sb.AppendLine("================================================");
            sb.AppendLine();

            lock (SyncLock)
            {
                sb.AppendLine($"--- CURRENT SESSION EVENTS ({RingBuffer.Count} max {MaxEntries}) ---");
                if (RingBuffer.Count == 0)
                {
                    sb.AppendLine("(No events recorded)");
                }
                else
                {
                    foreach (string entry in RingBuffer)
                    {
                        sb.AppendLine(entry);
                    }
                }
            }

            try
            {
                if (File.Exists(CrashLogFilePath))
                {
                    FileInfo fi = new(CrashLogFilePath);
                    if (fi.Length > 0 && fi.Length <= MaxLogFileBytes)
                    {
                        sb.AppendLine();
                        sb.AppendLine($"--- PREVIOUS PERSISTED SNAPSHOT ({fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}) ---");
                        sb.AppendLine(File.ReadAllText(CrashLogFilePath, Encoding.UTF8));
                    }
                }
            }
            catch { }

            return sb.ToString();
        }
    }
}
