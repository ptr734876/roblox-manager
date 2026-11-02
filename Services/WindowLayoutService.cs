using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace RobloxAccountManager.Services
{
    public static class WindowLayoutService
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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out RECT pvParam, uint fWinIni);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("shell32.dll")]
        private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        private const int SW_RESTORE = 9;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        // SWP_NOSENDCHANGING (0x0400) не дает окну Roblox перехватить WM_WINDOWPOSCHANGING
        // и заблокировать уменьшение окна ниже лимита 800x600!
        private const uint SWP_NOSENDCHANGING = 0x0400;
        private const uint SWP_ASYNCWINDOWPOS = 0x4000;

        private const uint MONITOR_DEFAULTTOPRIMARY = 1;
        private const uint SPI_GETWORKAREA = 0x0030;
        private const uint ABM_GETTASKBARPOS = 0x00000005;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (DPI_AWARENESS_CONTEXT)(-4)
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

        /// <summary>
        /// Находит все открытые игровые окна Roblox и расставляет их равномерной сеткой одинакового максимального размера
        /// на всю рабочую область экрана (не закрывая панель задач Windows и принудительно обходя лимит минимального размера 800x600).
        /// </summary>
        public static (int count, int cols, int rows, int width, int height) TileRobloxWindows(IntPtr referenceWindowHandle = default)
        {
            IntPtr previousDpiContext = IntPtr.Zero;
            try
            {
                previousDpiContext = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            }
            catch { }

            try
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
                    return (0, 0, 0, 0, 0);

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

                foreach (uint pid in robloxPids)
                {
                    if (windowByPid.ContainsKey(pid)) continue;
                    try
                    {
                        using Process p = Process.GetProcessById((int)pid);
                        if (p.MainWindowHandle != IntPtr.Zero)
                        {
                            windowByPid[pid] = (p.MainWindowHandle, 1);
                        }
                    }
                    catch { }
                }

                var windows = windowByPid
                    .OrderBy(kv => pidStartTimes.TryGetValue(kv.Key, out var t) ? t : DateTime.MaxValue)
                    .ThenBy(kv => kv.Key)
                    .Select(kv => kv.Value.Hwnd)
                    .ToList();

                int n = windows.Count;
                if (n == 0)
                    return (0, 0, 0, 0, 0);

                RECT workArea = GetExactWorkAreaExcludingTaskbar(referenceWindowHandle != IntPtr.Zero ? referenceWindowHandle : windows[0]);
                int workWidth = Math.Max(320, workArea.Right - workArea.Left);
                int workHeight = Math.Max(240, workArea.Bottom - workArea.Top);

                var (cols, rows) = ChooseOptimalGrid(n, workWidth, workHeight);

                int cellW = workWidth / cols;
                int cellH = workHeight / rows;

                uint flags = SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOSENDCHANGING;

                for (int i = 0; i < n; i++)
                {
                    IntPtr hWnd = windows[i];
                    int row = i / cols;
                    int col = i % cols;

                    // На последней колонке/строке добираем остаток деления, чтобы не было пустой полоски и не вылезало за край
                    int targetX = workArea.Left + col * cellW;
                    int targetY = workArea.Top + row * cellH;
                    int targetW = (col == cols - 1) ? (workArea.Right - targetX) : cellW;
                    int targetH = (row == rows - 1) ? (workArea.Bottom - targetY) : cellH;

                    try
                    {
                        if (IsIconic(hWnd) || IsZoomed(hWnd))
                        {
                            ShowWindow(hWnd, SW_RESTORE);
                        }

                        // Первый проход с SWP_NOSENDCHANGING (принудительно сжимает окно меньше 800x600!)
                        SetWindowPos(hWnd, IntPtr.Zero, targetX, targetY, targetW, targetH, flags);

                        // Второй проход: компенсируем невидимую теневую рамку Windows 10/11 (DWM Extended Frame Bounds),
                        // чтобы видимые границы окон стояли ровно по сетке и не заходили под панель задач
                        if (GetWindowRect(hWnd, out RECT winRect) &&
                            DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT dwmRect, Marshal.SizeOf<RECT>()) == 0)
                        {
                            int leftBorder = dwmRect.Left - winRect.Left;
                            int topBorder = dwmRect.Top - winRect.Top;
                            int rightBorder = winRect.Right - dwmRect.Right;
                            int bottomBorder = winRect.Bottom - dwmRect.Bottom;

                            if (leftBorder is >= 0 and <= 32 &&
                                rightBorder is >= 0 and <= 32 &&
                                bottomBorder is >= 0 and <= 32 &&
                                topBorder is >= -8 and <= 32)
                            {
                                int adjX = targetX - leftBorder;
                                int adjY = targetY - topBorder;
                                int adjW = targetW + leftBorder + rightBorder;
                                int adjH = targetH + topBorder + bottomBorder;

                                SetWindowPos(hWnd, IntPtr.Zero, adjX, adjY, adjW, adjH, flags);
                            }
                        }
                    }
                    catch { }
                }

                return (n, cols, rows, cellW, cellH);
            }
            finally
            {
                if (previousDpiContext != IntPtr.Zero)
                {
                    try { SetThreadDpiAwarenessContext(previousDpiContext); } catch { }
                }
            }
        }

        private static RECT GetExactWorkAreaExcludingTaskbar(IntPtr hWnd)
        {
            RECT screenRect = new() { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            RECT workRect = new() { Left = 0, Top = 0, Right = 1920, Bottom = 1032 };

            try
            {
                IntPtr hMonitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTOPRIMARY);
                if (hMonitor != IntPtr.Zero)
                {
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(hMonitor, ref mi))
                    {
                        screenRect = mi.rcMonitor;
                        workRect = mi.rcWork;
                    }
                }
                else if (SystemParametersInfo(SPI_GETWORKAREA, 0, out RECT spiWork, 0))
                {
                    workRect = spiWork;
                }
            }
            catch { }

            // Дополнительно проверяем реальное окно панели задач (Shell_TrayWnd и SHAppBarMessage)
            // на случай если панель задач в режиме автоскрытия или rcWork не вычел её высоту
            try
            {
                var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
                if (SHAppBarMessage(ABM_GETTASKBARPOS, ref abd) != IntPtr.Zero)
                {
                    SubtractTaskbarRect(ref workRect, screenRect, abd.rc);
                }
            }
            catch { }

            try
            {
                IntPtr trayHwnd = FindWindow("Shell_TrayWnd", null);
                if (trayHwnd != IntPtr.Zero && IsWindowVisible(trayHwnd) && GetWindowRect(trayHwnd, out RECT trayRect))
                {
                    SubtractTaskbarRect(ref workRect, screenRect, trayRect);
                }
            }
            catch { }

            // Если вдруг после всех проверок нижняя граница всё ещё равна полной высоте экрана,
            // резервируем стандартные 48 пикселей под нижний бар Windows
            if (workRect.Bottom >= screenRect.Bottom && (screenRect.Bottom - screenRect.Top) >= 600)
            {
                workRect.Bottom = screenRect.Bottom - 48;
            }

            return workRect;
        }

        private static void SubtractTaskbarRect(ref RECT workRect, RECT screenRect, RECT barRect)
        {
            int barW = barRect.Right - barRect.Left;
            int barH = barRect.Bottom - barRect.Top;
            if (barW <= 0 || barH <= 0) return;

            int screenW = screenRect.Right - screenRect.Left;
            int screenH = screenRect.Bottom - screenRect.Top;

            // Горизонтальная панель задач (внизу или вверху)
            if (barW >= screenW / 2 && barH is > 4 and <= 160)
            {
                int midY = (screenRect.Top + screenRect.Bottom) / 2;
                if (barRect.Top >= midY)
                {
                    // Панель задач внизу
                    if (barRect.Top < workRect.Bottom)
                        workRect.Bottom = barRect.Top;
                }
                else if (barRect.Bottom <= midY)
                {
                    // Панель задач вверху
                    if (barRect.Bottom > workRect.Top)
                        workRect.Top = barRect.Bottom;
                }
            }
            // Вертикальная панель задач (слева или справа)
            else if (barH >= screenH / 2 && barW is > 4 and <= 240)
            {
                int midX = (screenRect.Left + screenRect.Right) / 2;
                if (barRect.Left >= midX)
                {
                    if (barRect.Left < workRect.Right)
                        workRect.Right = barRect.Left;
                }
                else if (barRect.Right <= midX)
                {
                    if (barRect.Right > workRect.Left)
                        workRect.Left = barRect.Right;
                }
            }
        }

        private static (int cols, int rows) ChooseOptimalGrid(int n, int workWidth, int workHeight)
        {
            if (n <= 1) return (1, 1);
            if (n == 2) return workWidth >= workHeight ? (2, 1) : (1, 2);

            int bestCols = 1;
            int bestRows = n;
            double bestScore = double.NegativeInfinity;

            for (int c = 1; c <= n; c++)
            {
                int r = (int)Math.Ceiling((double)n / c);
                if ((r - 1) * c >= n)
                    continue;

                double cellW = (double)workWidth / c;
                double cellH = (double)workHeight / r;
                double fillFraction = (double)n / (c * r);

                double aspect = cellW / cellH;
                double targetAspect = 16.0 / 10.0;
                double aspectDistance = Math.Abs(Math.Log(aspect / targetAspect));

                double score = fillFraction * 2.5 - aspectDistance * 0.45;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestCols = c;
                    bestRows = r;
                }
            }

            return (bestCols, bestRows);
        }
    }
}
