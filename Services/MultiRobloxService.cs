using System;
using System.Diagnostics;
using System.Threading;

namespace RobloxAccountManager.Services
{
    public class MultiRobloxService : IDisposable
    {
        private Thread? _workerThread;
        private readonly ManualResetEvent _stopEvent = new(false);
        private int _totalHandlesClosed;

        public bool IsEnabled { get; private set; }
        public int TotalHandlesClosed => Volatile.Read(ref _totalHandlesClosed);

        public void Enable()
        {
            if (IsEnabled)
                return;

            _stopEvent.Reset();
            _workerThread = new Thread(() =>
            {
                while (!_stopEvent.WaitOne(900))
                {
                    try
                    {
                        int closed = HandleCloser.CloseRobloxSingletonHandles();
                        if (closed > 0)
                            Interlocked.Add(ref _totalHandlesClosed, closed);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Не удалось освободить ROBLOX_singletonEvent: {ex}");
                    }
                }
            })
            {
                IsBackground = true,
                Name = "RobloxSingletonCloserThread"
            };

            _workerThread.Start();
            IsEnabled = true;
        }

        public void Disable()
        {
            if (!IsEnabled)
                return;

            _stopEvent.Set();
            _workerThread?.Join(1000);
            _workerThread = null;
            IsEnabled = false;
        }

        /// <summary>
        /// Освобождает singleton перед запуском. Не завершает процессы Roblox по эвристике
        /// памяти/окна: такие проверки могут принять уже работающий клиент за tray-процесс.
        /// </summary>
        public int PrepareForNewInstanceLaunch()
        {
            if (!IsEnabled)
                return 0;

            int closed = HandleCloser.CloseRobloxSingletonHandles();
            Interlocked.Add(ref _totalHandlesClosed, closed);
            return closed;
        }

        /// <summary>
        /// Возвращает реальное количество открытых игровых окон Roblox (потребляющих > 250 МБ ОЗУ),
        /// игнорируя короткоживущие стартовые процессы и фоновый трей (--launch-to-tray ~170 МБ).
        /// </summary>
        public int GetRunningRobloxCount()
        {
            int count = 0;
            try
            {
                var procs = Process.GetProcessesByName("RobloxPlayerBeta");
                foreach (var p in procs)
                {
                    try
                    {
                        if (p.WorkingSet64 > 250L * 1024 * 1024)
                        {
                            count++;
                        }
                    }
                    catch { }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch
            {
                return 0;
            }
            return count;
        }

        public void CloseAllRobloxInstances()
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName("RobloxPlayerBeta"))
                {
                    try { proc.Kill(); } catch { }
                    finally { proc.Dispose(); }
                }
            }
            catch { }
        }

        public void Dispose()
        {
            Disable();
            _stopEvent.Dispose();
        }
    }
}
