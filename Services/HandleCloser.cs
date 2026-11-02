using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace RobloxAccountManager.Services
{
    public static class HandleCloser
    {
        private const int SystemExtendedHandleInformation = 64;
        private const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;
        private const uint DUPLICATE_CLOSE_SOURCE = 0x00000001;
        private const uint DUPLICATE_SAME_ACCESS = 0x00000002;
        private const uint PROCESS_DUP_HANDLE = 0x0040;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const int ObjectNameInformation = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX
        {
            public IntPtr Object;
            public IntPtr UniqueProcessId;
            public IntPtr HandleValue;
            public uint GrantedAccess;
            public ushort CreatorBackTraceIndex;
            public ushort ObjectTypeIndex;
            public uint HandleAttributes;
            public uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [DllImport("ntdll.dll")]
        private static extern uint NtQuerySystemInformation(
            int SystemInformationClass,
            IntPtr SystemInformation,
            int SystemInformationLength,
            out int ReturnLength);

        [DllImport("ntdll.dll")]
        private static extern uint NtQueryObject(
            IntPtr Handle,
            int ObjectInformationClass,
            IntPtr ObjectInformation,
            int ObjectInformationLength,
            out int ReturnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
            uint dwDesiredAccess,
            bool bInheritHandle,
            int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(
            IntPtr hSourceProcessHandle,
            IntPtr hSourceHandle,
            IntPtr hTargetProcessHandle,
            out IntPtr lpTargetHandle,
            uint dwDesiredAccess,
            bool bInheritHandle,
            uint dwOptions);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private static ushort _cachedEventTypeIndex;

        private static void EnsureEventTypeIndex()
        {
            if (_cachedEventTypeIndex != 0)
                return;

            using var tempEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
            IntPtr evtHandle = tempEvent.SafeWaitHandle.DangerousGetHandle();
            int currentPid = Environment.ProcessId;

            IntPtr infoPtr = QuerySystemHandleTable(out long handleCount);
            if (infoPtr == IntPtr.Zero)
                return;

            try
            {
                int offset = IntPtr.Size * 2;
                int entrySize = Marshal.SizeOf<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>();

                for (long i = 0; i < handleCount; i++)
                {
                    IntPtr entryPtr = IntPtr.Add(infoPtr, offset + (int)(i * entrySize));
                    var entry = Marshal.PtrToStructure<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>(entryPtr);

                    if ((int)entry.UniqueProcessId == currentPid && entry.HandleValue == evtHandle)
                    {
                        _cachedEventTypeIndex = entry.ObjectTypeIndex;
                        break;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
            }
        }

        /// <summary>
        /// Находит в процессах RobloxPlayerBeta хэндл типа Event с именем \Sessions\*\BaseNamedObjects\ROBLOX_singletonEvent
        /// и закрывает ТОЛЬКО его через DuplicateHandle(..., DUPLICATE_CLOSE_SOURCE), в точности как в Process Hacker / System Informer.
        /// </summary>
        public static int CloseRobloxSingletonHandles(int? excludePid = null)
        {
            var robloxProcs = Process.GetProcessesByName("RobloxPlayerBeta");
            if (robloxProcs.Length == 0)
                return 0;

            var targetPids = new HashSet<int>();
            foreach (var p in robloxProcs)
            {
                try
                {
                    if (excludePid.HasValue && p.Id == excludePid.Value)
                        continue;

                    // Не закрываем singleton у только что стартовавшего процесса (< 65 МБ ОЗУ и без окна),
                    // пока он проходит начальную распаковку / FlagFetchingStarterModule
                    if (p.WorkingSet64 > 65L * 1024 * 1024 || p.MainWindowHandle != IntPtr.Zero)
                    {
                        targetPids.Add(p.Id);
                    }
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }

            if (targetPids.Count == 0)
                return 0;

            EnsureEventTypeIndex();
            if (_cachedEventTypeIndex == 0)
                return 0;

            IntPtr infoPtr = QuerySystemHandleTable(out long handleCount);
            if (infoPtr == IntPtr.Zero)
                return 0;

            int closedCount = 0;
            var processHandles = new Dictionary<int, IntPtr>();

            try
            {
                int offset = IntPtr.Size * 2;
                int entrySize = Marshal.SizeOf<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>();
                IntPtr currentProcess = GetCurrentProcess();

                for (long i = 0; i < handleCount; i++)
                {
                    IntPtr entryPtr = IntPtr.Add(infoPtr, offset + (int)(i * entrySize));
                    var entry = Marshal.PtrToStructure<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>(entryPtr);
                    int pid = (int)entry.UniqueProcessId;

                    if (!targetPids.Contains(pid))
                        continue;

                    // Проверяем ТОЛЬКО хэндлы типа Event (21) — ровно один хэндл ROBLOX_singletonEvent!
                    if (entry.ObjectTypeIndex != _cachedEventTypeIndex)
                        continue;

                    if (!processHandles.TryGetValue(pid, out IntPtr hProc))
                    {
                        hProc = OpenProcess(PROCESS_DUP_HANDLE | PROCESS_QUERY_INFORMATION, false, pid);
                        processHandles[pid] = hProc;
                    }

                    if (hProc == IntPtr.Zero)
                        continue;

                    if (!DuplicateHandle(hProc, entry.HandleValue, currentProcess, out IntPtr dupHandle, 0, false, DUPLICATE_SAME_ACCESS))
                        continue;

                    try
                    {
                        string objName = QueryHandleName(dupHandle);
                        if (!string.IsNullOrEmpty(objName) &&
                            objName.IndexOf("ROBLOX_singletonEvent", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (DuplicateHandle(hProc, entry.HandleValue, IntPtr.Zero, out IntPtr dummy, 0, false, DUPLICATE_CLOSE_SOURCE))
                            {
                                if (dummy != IntPtr.Zero) CloseHandle(dummy);
                                closedCount++;
                            }
                        }
                    }
                    finally
                    {
                        CloseHandle(dupHandle);
                    }
                }
            }
            catch
            {
                // Ignore errors
            }
            finally
            {
                foreach (var kv in processHandles)
                {
                    if (kv.Value != IntPtr.Zero)
                        CloseHandle(kv.Value);
                }
                Marshal.FreeHGlobal(infoPtr);
            }

            return closedCount;
        }

        private static string QueryHandleName(IntPtr handle)
        {
            int bufLen = 1024;
            IntPtr buf = Marshal.AllocHGlobal(bufLen);
            try
            {
                uint status = NtQueryObject(handle, ObjectNameInformation, buf, bufLen, out _);
                if (status != 0)
                    return string.Empty;

                var ustr = Marshal.PtrToStructure<UNICODE_STRING>(buf);
                if (ustr.Length == 0 || ustr.Buffer == IntPtr.Zero)
                    return string.Empty;

                return Marshal.PtrToStringUni(ustr.Buffer, ustr.Length / 2);
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        private static IntPtr QuerySystemHandleTable(out long handleCount)
        {
            handleCount = 0;
            int length = 0x200000;
            IntPtr infoPtr = IntPtr.Zero;

            while (true)
            {
                infoPtr = Marshal.AllocHGlobal(length);
                uint status = NtQuerySystemInformation(SystemExtendedHandleInformation, infoPtr, length, out int returnLength);
                if (status == STATUS_INFO_LENGTH_MISMATCH)
                {
                    Marshal.FreeHGlobal(infoPtr);
                    length = Math.Max(length * 2, returnLength + 0x40000);
                    continue;
                }
                if (status != 0)
                {
                    Marshal.FreeHGlobal(infoPtr);
                    return IntPtr.Zero;
                }
                break;
            }

            handleCount = Marshal.ReadInt64(infoPtr, 0);
            return infoPtr;
        }
    }
}
