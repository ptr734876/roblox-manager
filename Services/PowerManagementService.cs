using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace RobloxAccountManager.Services
{
    public sealed class PowerManagementService : IDisposable
    {
        [Flags]
        private enum EXECUTION_STATE : uint
        {
            ES_SYSTEM_REQUIRED = 0x00000001,
            ES_DISPLAY_REQUIRED = 0x00000002,
            ES_AWAYMODE_REQUIRED = 0x00000040,
            ES_CONTINUOUS = 0x80000000
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

        // GUID подгруппы кнопок питания и закрытия крышки (SUB_BUTTONS) и действия крышки (LIDACTION)
        private const string SubButtonsGuid = "4f971e89-eebd-4455-a8de-9e59040e7347";
        private const string LidActionGuid = "5ca83367-6e45-459f-a27b-476b1d01c936";

        private int? _savedLidAcValue;
        private int? _savedLidDcValue;
        private bool _isEnabled;

        public bool IsEnabled => _isEnabled;

        public void Enable(int? fallbackLidAc = null, int? fallbackLidDc = null)
        {
            // 1. Запрещаем Windows уходить в сон от бездействия и выключать экран/сеть
            try
            {
                SetThreadExecutionState(
                    EXECUTION_STATE.ES_CONTINUOUS |
                    EXECUTION_STATE.ES_SYSTEM_REQUIRED |
                    EXECUTION_STATE.ES_DISPLAY_REQUIRED |
                    EXECUTION_STATE.ES_AWAYMODE_REQUIRED);
            }
            catch { }

            // 2. Сохраняем текущие настройки закрытия крышки (если еще не сохранены) и ставим 0 ("Действие не требуется")
            try
            {
                if (_savedLidAcValue == null || _savedLidDcValue == null)
                {
                    var (ac, dc) = QueryCurrentLidAction();
                    if (ac.HasValue && ac.Value != 0)
                        _savedLidAcValue = ac.Value;
                    else if (fallbackLidAc.HasValue && fallbackLidAc.Value > 0)
                        _savedLidAcValue = fallbackLidAc.Value;
                    else
                        _savedLidAcValue ??= 1; // По умолчанию 1 = Сон

                    if (dc.HasValue && dc.Value != 0)
                        _savedLidDcValue = dc.Value;
                    else if (fallbackLidDc.HasValue && fallbackLidDc.Value > 0)
                        _savedLidDcValue = fallbackLidDc.Value;
                    else
                        _savedLidDcValue ??= 1; // По умолчанию 1 = Сон
                }

                RunPowerCfg($"/setacvalueindex SCHEME_CURRENT {SubButtonsGuid} {LidActionGuid} 0");
                RunPowerCfg($"/setdcvalueindex SCHEME_CURRENT {SubButtonsGuid} {LidActionGuid} 0");
                RunPowerCfg("/setactive SCHEME_CURRENT");
            }
            catch { }

            _isEnabled = true;
        }

        public void Disable()
        {
            // 1. Возвращаем стандартное поведение сна при бездействии
            try
            {
                SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
            }
            catch { }

            // 2. Возвращаем исходное поведение при закрытии крышки ноутбука
            if (_isEnabled || _savedLidAcValue.HasValue || _savedLidDcValue.HasValue)
            {
                try
                {
                    int acRestore = _savedLidAcValue ?? 1;
                    int dcRestore = _savedLidDcValue ?? 1;
                    RunPowerCfg($"/setacvalueindex SCHEME_CURRENT {SubButtonsGuid} {LidActionGuid} {acRestore}");
                    RunPowerCfg($"/setdcvalueindex SCHEME_CURRENT {SubButtonsGuid} {LidActionGuid} {dcRestore}");
                    RunPowerCfg("/setactive SCHEME_CURRENT");
                }
                catch { }
            }

            _isEnabled = false;
        }

        public (int? SavedAc, int? SavedDc) GetSavedLidValues() => (_savedLidAcValue, _savedLidDcValue);

        private static (int? AcValue, int? DcValue) QueryCurrentLidAction()
        {
            try
            {
                string output = RunPowerCfgWithOutput($"/qh SCHEME_CURRENT {SubButtonsGuid} {LidActionGuid}");
                var matches = Regex.Matches(output, @"0x([0-9a-fA-F]{8})");
                if (matches.Count >= 2)
                {
                    int ac = Convert.ToInt32(matches[^2].Groups[1].Value, 16);
                    int dc = Convert.ToInt32(matches[^1].Groups[1].Value, 16);
                    return (ac, dc);
                }
            }
            catch { }
            return (null, null);
        }

        private static void RunPowerCfg(string arguments)
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "powercfg",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            proc?.WaitForExit(3000);
        }

        private static string RunPowerCfgWithOutput(string arguments)
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "powercfg",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (proc == null) return string.Empty;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            return output;
        }

        public void Dispose()
        {
            if (_isEnabled)
            {
                Disable();
            }
        }
    }
}
