using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ComponentModel;

namespace RobloxAccountManager.Services
{
    [SupportedOSPlatform("windows")]
    public sealed class RobloxProcessResourceService
    {
        private readonly Dictionary<int, OriginalPriority> _originalPriorities = new();

        public RobloxProcessResourceResult UpdateBackgroundPriority(bool enabled)
        {
            if (!enabled)
                return RestoreOriginalPriorities();

            int errors = 0;
            int loweredCount = 0;
            var seenProcessIds = new HashSet<int>();
            uint foregroundProcessId = 0;
            IntPtr foregroundWindow = GetForegroundWindow();
            if (foregroundWindow != IntPtr.Zero)
                GetWindowThreadProcessId(foregroundWindow, out foregroundProcessId);

            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName("RobloxPlayerBeta");
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                return new RobloxProcessResourceResult(0, 1);
            }

            foreach (Process process in processes)
            {
                try
                {
                    int processId = process.Id;
                    seenProcessIds.Add(processId);
                    DateTime startTime = process.StartTime.ToUniversalTime();
                    if (process.MainWindowHandle == IntPtr.Zero)
                    {
                        if (_originalPriorities.TryGetValue(processId, out OriginalPriority tracked) &&
                            tracked.StartTime == startTime &&
                            process.PriorityClass != tracked.PriorityClass)
                        {
                            process.PriorityClass = tracked.PriorityClass;
                        }

                        _originalPriorities.Remove(processId);
                        continue;
                    }

                    if (!_originalPriorities.TryGetValue(processId, out OriginalPriority original) ||
                        original.StartTime != startTime)
                    {
                        original = new OriginalPriority(startTime, process.PriorityClass);
                        _originalPriorities[processId] = original;
                    }

                    ProcessPriorityClass desiredPriority = processId == foregroundProcessId
                        ? original.PriorityClass
                        : ProcessPriorityClass.BelowNormal;

                    if (process.PriorityClass != desiredPriority)
                        process.PriorityClass = desiredPriority;

                    if (desiredPriority == ProcessPriorityClass.BelowNormal)
                        loweredCount++;
                }
                catch (Exception ex) when (
                    ex is Win32Exception or InvalidOperationException or NotSupportedException or
                    UnauthorizedAccessException)
                {
                    errors++;
                }
                finally
                {
                    process.Dispose();
                }
            }

            foreach (int processId in new List<int>(_originalPriorities.Keys))
            {
                if (!seenProcessIds.Contains(processId))
                    _originalPriorities.Remove(processId);
            }

            return new RobloxProcessResourceResult(loweredCount, errors);
        }

        public RobloxProcessResourceResult RestoreOriginalPriorities()
        {
            if (_originalPriorities.Count == 0)
                return new RobloxProcessResourceResult(0, 0);

            int errors = 0;
            var processesById = new Dictionary<int, Process>();
            try
            {
                foreach (Process process in Process.GetProcessesByName("RobloxPlayerBeta"))
                    processesById[process.Id] = process;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                foreach (Process process in processesById.Values)
                    process.Dispose();
                _originalPriorities.Clear();
                return new RobloxProcessResourceResult(0, 1);
            }

            foreach (var entry in _originalPriorities)
            {
                if (!processesById.TryGetValue(entry.Key, out Process? process))
                    continue;

                try
                {
                    if (process.StartTime.ToUniversalTime() == entry.Value.StartTime &&
                        process.PriorityClass != entry.Value.PriorityClass)
                    {
                        process.PriorityClass = entry.Value.PriorityClass;
                    }
                }
                catch (Exception ex) when (
                    ex is Win32Exception or InvalidOperationException or NotSupportedException or
                    UnauthorizedAccessException)
                {
                    errors++;
                }
            }

            foreach (Process process in processesById.Values)
                process.Dispose();

            _originalPriorities.Clear();
            return new RobloxProcessResourceResult(0, errors);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        private readonly record struct OriginalPriority(DateTime StartTime, ProcessPriorityClass PriorityClass);
    }

    public readonly record struct RobloxProcessResourceResult(int LoweredBackgroundCount, int ErrorCount);
}
