using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services
{
    public class AppConfigData
    {
        public List<RobloxAccount> Accounts { get; set; } = new();
        public List<SavedPlace> SavedPlaces { get; set; } = new();
        public RobloxGameSettings? RobloxSettings { get; set; }
        public string LastPlaceInput { get; set; } = string.Empty;
        public string LastVipOrJobInput { get; set; } = string.Empty;
        public int MultiLaunchDelaySeconds { get; set; } = 3;
        public bool MultiRobloxEnabled { get; set; } = true;
        public bool AutoReconnectEnabled { get; set; } = false;
        public bool PreventSleepAndLidEnabled { get; set; } = false;
        public bool AntiAfkEnabled { get; set; } = false;
        public int? SavedLidAcValue { get; set; }
        public int? SavedLidDcValue { get; set; }
    }

    public class RobloxGameSettings
    {
        public bool AutomaticGraphics { get; set; } = true;
        public int GraphicsQualityLevel { get; set; } = 6;
        public float MasterVolume { get; set; } = 1f;
        public float MouseSensitivity { get; set; } = 1f;
        public bool LowerBackgroundProcessPriority { get; set; }
        public int FrameRateCap { get; set; } = -1;

        // Таймауты запуска и авто-переподключения (в секундах)
        public int LaunchTimeoutSeconds { get; set; } = 210;
        public int StartupGracePeriodSeconds { get; set; } = 180;
        public int TeleportProtectionSeconds { get; set; } = 150;
        public int DisconnectConfirmSeconds { get; set; } = 20;
        public int AntiAfkIntervalMinutes { get; set; } = 12;
    }

    public class AccountStorageService
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RobloxAccountManager_DPAPI_Salt_v1");
        private readonly string _dataFolder;
        private readonly string _dataFilePath;
        private readonly string _backupFilePath;

        public AccountStorageService()
        {
            _dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RobloxAccountManager");

            if (!Directory.Exists(_dataFolder))
            {
                Directory.CreateDirectory(_dataFolder);
            }

            _dataFilePath = Path.Combine(_dataFolder, "accounts_data.json");
            _backupFilePath = Path.Combine(_dataFolder, "accounts_data.bak.json");
        }

        public string DataFolder => _dataFolder;

        public static string EncryptCookie(string plainCookie)
        {
            if (string.IsNullOrWhiteSpace(plainCookie))
                return string.Empty;

            byte[] plainBytes = Encoding.UTF8.GetBytes(plainCookie.Trim());
            byte[] encryptedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encryptedBytes);
        }

        public static string DecryptCookie(string encryptedBase64)
        {
            if (string.IsNullOrWhiteSpace(encryptedBase64))
                return string.Empty;

            try
            {
                byte[] encryptedBytes = Convert.FromBase64String(encryptedBase64);
                byte[] plainBytes = ProtectedData.Unprotect(encryptedBytes, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        public AppConfigData Load()
        {
            var data = TryReadConfigFile(_dataFilePath) ?? TryReadConfigFile(_backupFilePath);
            if (data == null)
            {
                return CreateDefaultConfig();
            }

            data.Accounts ??= new List<RobloxAccount>();
            data.SavedPlaces ??= new List<SavedPlace>();
            bool changed = data.SavedPlaces.RemoveAll(IsBuiltInSavedPlace) > 0;
            if (!string.IsNullOrEmpty(data.LastPlaceInput) || !string.IsNullOrEmpty(data.LastVipOrJobInput))
            {
                data.LastPlaceInput = string.Empty;
                data.LastVipOrJobInput = string.Empty;
                changed = true;
            }

            if (changed)
            {
                try { Save(data); } catch { }
            }

            return data;
        }

        private static AppConfigData? TryReadConfigFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return null;

                string json = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                    return null;

                return JsonSerializer.Deserialize<AppConfigData>(json);
            }
            catch (Exception ex)
            {
                DiagnosticsService.LogException("Storage.Load", ex, flushToDisk: false, customPrefix: Path.GetFileName(path));
                return null;
            }
        }

        /// <summary>
        /// Атомарное сохранение конфигурации с созданием резервной копии (.bak.json),
        /// чтобы ни при обновлении, ни при отключении питания настройки и куки никогда не повредились.
        /// </summary>
        public void Save(AppConfigData data)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(data, options);

            string tempPath = _dataFilePath + ".tmp";
            File.WriteAllText(tempPath, json, Encoding.UTF8);

            if (File.Exists(_dataFilePath))
            {
                try
                {
                    File.Copy(_dataFilePath, _backupFilePath, overwrite: true);
                }
                catch { }
            }

            File.Move(tempPath, _dataFilePath, overwrite: true);
        }

        private static AppConfigData CreateDefaultConfig()
        {
            return new AppConfigData();
        }

        private static bool IsBuiltInSavedPlace(SavedPlace place)
        {
            return (place.Name == "Blox Fruits" && place.PlaceId == "2753915549") ||
                   (place.Name == "Adopt Me!" && place.PlaceId == "920587237") ||
                   (place.Name == "Bee Swarm Simulator" && place.PlaceId == "1537690962") ||
                   (place.Name == "Brookhaven RP" && place.PlaceId == "4924922222");
        }
    }
}
