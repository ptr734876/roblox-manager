using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace RobloxAccountManager.Services
{
    public sealed class AntiAfkService
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public MOUSEINPUT mi;
            [FieldOffset(0)]
            public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        private const int SW_SHOWNOACTIVATE = 4;
        private const int SW_RESTORE = 9;
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_SCANCODE = 0x0008;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        // VK_RCONTROL (Right Ctrl) — абсолютно безопасная клавиша в Roblox:
        // не двигает персонажа, не меняет камеру/зум, не открывает чат/инвентарь/меню, не использует предметы,
        // но гарантированно сбрасывает 20-минутный таймер Roblox Idled (Error 267 / 273).
        private const ushort VK_RCONTROL = 0xA3;
        private const ushort SCANCODE_RCONTROL = 0x1D;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

        private const uint WM_ACTIVATE = 0x0006;
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private static readonly IntPtr WA_ACTIVE = new(1);

        private bool _isRunningPulse;

        /// <summary>
        /// Возвращает время в секундах с момента последнего физического движения мыши или нажатия клавиши пользователем.
        /// </summary>
        public static double GetUserIdleSeconds()
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref lii))
                return 999.0;

            uint elapsedTicks = unchecked((uint)Environment.TickCount - lii.dwTime);
            return elapsedTicks / 1000.0;
        }

        /// <summary>
        /// Проходит по всем открытым окнам Roblox, на мгновение передает каждому фокус и посылает безопасное нажатие
        /// клавиши Right Ctrl (аппаратный скан-код + сообщение окну), после чего возвращает фокус исходному окну.
        /// </summary>
        public async Task<int> SendAntiAfkPulseToAllWindowsAsync(CancellationToken cancellationToken = default)
        {
            if (_isRunningPulse) return 0;
            _isRunningPulse = true;

            try
            {
                var windows = FindRobloxWindows();
                if (windows.Count == 0)
                    return 0;

                IntPtr originalForeground = GetForegroundWindow();
                int pulsedCount = 0;

                foreach (IntPtr hWnd in windows)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    try
                    {
                        if (IsIconic(hWnd))
                        {
                            ShowWindow(hWnd, SW_RESTORE);
                            await Task.Delay(80, cancellationToken);
                        }

                        ForceForegroundWindow(hWnd);
                        PostMessage(hWnd, WM_ACTIVATE, WA_ACTIVE, IntPtr.Zero);
                        await Task.Delay(75, cancellationToken);

                        // 1. Аппаратный скан-код Right Ctrl через SendInput (считывается DirectInput / RawInput движка Roblox)
                        SendRightCtrlHardwareKey(keyUp: false);
                        // 2. Дублируем WM_KEYDOWN/WM_KEYUP напрямую в очередь окна
                        IntPtr lParamDown = new(1 | (SCANCODE_RCONTROL << 16) | (1 << 24));
                        PostMessage(hWnd, WM_KEYDOWN, new IntPtr(VK_RCONTROL), lParamDown);

                        await Task.Delay(55, cancellationToken);

                        SendRightCtrlHardwareKey(keyUp: true);
                        IntPtr lParamUp = new(1 | (SCANCODE_RCONTROL << 16) | (1 << 24) | (3 << 30));
                        PostMessage(hWnd, WM_KEYUP, new IntPtr(VK_RCONTROL), lParamUp);

                        await Task.Delay(65, cancellationToken);
                        pulsedCount++;
                    }
                    catch { }
                }

                // Возвращаем фокус тому окну, которое было активно до импульса
                if (originalForeground != IntPtr.Zero && IsWindowVisible(originalForeground))
                {
                    try
                    {
                        ForceForegroundWindow(originalForeground);
                    }
                    catch { }
                }

                return pulsedCount;
            }
            finally
            {
                _isRunningPulse = false;
            }
        }

        private static void ForceForegroundWindow(IntPtr hWnd)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == hWnd) return;

            uint curThread = GetCurrentThreadId();
            uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
            uint targetThread = GetWindowThreadProcessId(hWnd, out _);

            bool attachedFg = false;
            bool attachedTarget = false;
            try
            {
                if (fgThread != 0 && fgThread != curThread)
                    attachedFg = AttachThreadInput(curThread, fgThread, true);
                if (targetThread != 0 && targetThread != curThread)
                    attachedTarget = AttachThreadInput(curThread, targetThread, true);

                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
            }
            finally
            {
                if (attachedTarget)
                    AttachThreadInput(curThread, targetThread, false);
                if (attachedFg)
                    AttachThreadInput(curThread, fgThread, false);
            }
        }

        private static void SendRightCtrlHardwareKey(bool keyUp)
        {
            uint flags = KEYEVENTF_SCANCODE | KEYEVENTF_EXTENDEDKEY | (keyUp ? KEYEVENTF_KEYUP : 0u);
            var inputs = new INPUT[]
            {
                new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = VK_RCONTROL,
                            wScan = SCANCODE_RCONTROL,
                            dwFlags = flags,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    }
                }
            };
            SendInput(1, inputs, Marshal.SizeOf<INPUT>());
        }

        private static List<IntPtr> FindRobloxWindows()
        {
            var robloxPids = new HashSet<uint>();
            var pidStartTimes = new Dictionary<uint, DateTime>();

            foreach (Process p in Process.GetProcessesByName("RobloxPlayerBeta"))
            {
                try
                {
                    if (!p.HasExited && (p.WorkingSet64 > 40L * 1024 * 1024 || p.MainWindowHandle != IntPtr.Zero))
                    {
                        uint pid = (uint)p.Id;
                        robloxPids.Add(pid);
                        try { pidStartTimes[pid] = p.StartTime; }
                        catch { pidStartTimes[pid] = DateTime.MaxValue; }
                    }
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }

            if (robloxPids.Count == 0)
                return new List<IntPtr>();

            var windowByPid = new Dictionary<uint, (IntPtr Hwnd, int Area)>();

            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd))
                    return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (!robloxPids.Contains(pid))
                    return true;

                if (GetWindowRect(hWnd, out RECT r))
                {
                    int w = Math.Max(0, r.Right - r.Left);
                    int h = Math.Max(0, r.Bottom - r.Top);
                    if (IsIconic(hWnd) || (w >= 120 && h >= 90))
                    {
                        int area = IsIconic(hWnd) ? 1_000_000 : w * h;
                        if (!windowByPid.TryGetValue(pid, out var existing) || area > existing.Area)
                        {
                            windowByPid[pid] = (hWnd, area);
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);

            return windowByPid
                .OrderBy(kv => pidStartTimes.TryGetValue(kv.Key, out var t) ? t : DateTime.MaxValue)
                .ThenBy(kv => kv.Key)
                .Select(kv => kv.Value.Hwnd)
                .ToList();
        }
    }
}
