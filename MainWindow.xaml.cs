using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RobloxAccountManager.Models;
using RobloxAccountManager.Services;
using RobloxAccountManager.Views;

namespace RobloxAccountManager
{
    public partial class MainWindow : Window
    {
        private readonly AccountStorageService _storageService = new();
        private readonly RobloxApiService _apiService = new();
        private readonly MultiRobloxService _multiRobloxService = new();
        private readonly RobloxSettingsService _robloxSettingsService = new();
        private readonly RobloxProcessResourceService _robloxProcessResourceService = new();
        private readonly PowerManagementService _powerManagementService = new();
        private readonly AntiAfkService _antiAfkService = new();
        private readonly GitHubUpdateService _updateService = new();
        private readonly DispatcherTimer _antiAfkTimer = new() { Interval = TimeSpan.FromSeconds(15) };
        private DateTime _lastAntiAfkPulseUtc = DateTime.UtcNow;
        private readonly SemaphoreSlim _launchLock = new(1, 1);
        private DateTime _lastLaunchTime = DateTime.MinValue;
        private GitHubReleaseInfo? _cachedAvailableUpdate;

        private AppConfigData _config = new();
        private readonly ObservableCollection<RobloxAccount> _filteredAccounts = new();
        private readonly DispatcherTimer _processMonitorTimer = new();
        private bool _isInitializing = true;
        private RobloxGameSettings? _lastAppliedRobloxSettings;
        private string? _lastRobloxSettingsNotice;

        public MainWindow()
        {
            InitializeComponent();
            AccountsListBox.ItemsSource = _filteredAccounts;

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private Process? _activeBootingProcess;

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _config = _storageService.Load();

            PlaceInputTextBox.Text = string.Empty;
            VipOrJobTextBox.Text = string.Empty;
            int defaultDelay = (_config.MultiLaunchDelaySeconds >= 1 && _config.MultiLaunchDelaySeconds <= 60) ? _config.MultiLaunchDelaySeconds : 6;
            DelaySecondsBox.Text = defaultDelay.ToString();

            MultiRobloxToggle.IsChecked = _config.MultiRobloxEnabled;
            AutoReconnectCheckBox.IsChecked = _config.AutoReconnectEnabled;
            PreventSleepCheckBox.IsChecked = _config.PreventSleepAndLidEnabled;
            AntiAfkCheckBox.IsChecked = _config.AntiAfkEnabled;
            if (_config.MultiRobloxEnabled)
            {
                _multiRobloxService.Enable();
                UpdateMultiRobloxIndicator(true);
            }
            else
            {
                UpdateMultiRobloxIndicator(false);
            }

            if (_config.PreventSleepAndLidEnabled)
            {
                _powerManagementService.Enable(_config.SavedLidAcValue, _config.SavedLidDcValue);
                var (savedAc, savedDc) = _powerManagementService.GetSavedLidValues();
                _config.SavedLidAcValue = savedAc;
                _config.SavedLidDcValue = savedDc;
            }

            RefreshSavedPlacesCombo();
            RefreshGroupFilterCombo();
            ApplyFilters();

            _isInitializing = false;

            Version ver = GitHubUpdateService.GetCurrentVersion();
            AppVersionText.Text = $"v{ver.Major}.{ver.Minor}.{ver.Build}";

            _processMonitorTimer.Interval = TimeSpan.FromSeconds(1.5);
            _processMonitorTimer.Tick += (_, _) =>
            {
                int count = _multiRobloxService.GetRunningRobloxCount();
                RobloxProcessResourceResult resourceResult =
                    _robloxProcessResourceService.UpdateBackgroundPriority(
                        _config.RobloxSettings?.LowerBackgroundProcessPriority == true);
                string resourceStatus = resourceResult.LoweredBackgroundCount > 0
                    ? $" • Эко-фон: {resourceResult.LoweredBackgroundCount}"
                    : string.Empty;
                RunningInstancesText.Text = $"Окон: {count}{resourceStatus}";
                int errCount = DiagnosticsService.ErrorCount;
                ExportLogsBtn.Content = errCount > 0 ? $"📋 Лог ({errCount})" : "📋 Лог ошибок";
            };
            _processMonitorTimer.Start();
            AttachToExistingRobloxSessions();
            _disconnectMonitorTimer.Tick += DisconnectMonitorTimer_Tick;
            _disconnectMonitorTimer.Start();
            _antiAfkTimer.Tick += AntiAfkTimer_Tick;
            _antiAfkTimer.Start();

            int attachedCount = _sessions.Count;
            FooterStatusText.Text = attachedCount > 0
                ? $"Аккаунтов: {_config.Accounts.Count} • Подхвачено окон: {attachedCount}"
                : $"Аккаунтов: {_config.Accounts.Count} • Готов к запуску";

            _ = CheckForUpdatesSilentlyAsync();
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            SaveCurrentConfig();
            _processMonitorTimer.Stop();
            _disconnectMonitorTimer.Stop();
            _antiAfkTimer.Stop();
            _robloxProcessResourceService.RestoreOriginalPriorities();
            _powerManagementService.Dispose();
            _multiRobloxService.Dispose();
        }

        private bool SaveCurrentConfig()
        {
            if (_isInitializing) return false;

            if (int.TryParse(DelaySecondsBox.Text.Trim(), out int delay) && delay >= 1 && delay <= 60)
            {
                _config.MultiLaunchDelaySeconds = delay;
            }
            _config.MultiRobloxEnabled = MultiRobloxToggle.IsChecked == true;
            _config.AutoReconnectEnabled = AutoReconnectCheckBox.IsChecked == true;
            _config.PreventSleepAndLidEnabled = PreventSleepCheckBox.IsChecked == true;
            _config.AntiAfkEnabled = AntiAfkCheckBox.IsChecked == true;
            var (savedAc, savedDc) = _powerManagementService.GetSavedLidValues();
            if (savedAc.HasValue) _config.SavedLidAcValue = savedAc;
            if (savedDc.HasValue) _config.SavedLidDcValue = savedDc;
            try
            {
                _storageService.Save(_config);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FooterStatusText.Text = $"Не удалось сохранить конфигурацию: {ex.Message}";
                return false;
            }
        }

        private void UpdateMultiRobloxIndicator(bool enabled)
        {
            if (MultiMutexIndicator == null) return;
            MultiMutexIndicator.Fill = enabled
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#22C55E"))
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
        }

        private void MultiRobloxToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            bool enable = MultiRobloxToggle.IsChecked == true;
            if (enable)
            {
                _multiRobloxService.Enable();
                FooterStatusText.Text = "Мульти-Roblox включен: можно запускать несколько аккаунтов одновременно.";
            }
            else
            {
                _multiRobloxService.Disable();
                FooterStatusText.Text = "Мульти-Roblox выключен.";
            }

            UpdateMultiRobloxIndicator(enable);
            SaveCurrentConfig();
        }

        private static readonly SavedPlace EmptySavedPlaceItem = new()
        {
            Name = "— Не выбрано —",
            PlaceId = string.Empty,
            VipLinkOrJobId = string.Empty
        };

        private bool _isUpdatingPlaceInputsFromCombo;

        private void RefreshSavedPlacesCombo()
        {
            var items = new List<SavedPlace> { EmptySavedPlaceItem };
            items.AddRange(_config.SavedPlaces);
            SavedPlacesCombo.ItemsSource = null;
            SavedPlacesCombo.ItemsSource = items;
            SavedPlacesCombo.SelectedItem = EmptySavedPlaceItem;
        }

        private void RefreshGroupFilterCombo()
        {
            string currentSelection = GroupFilterCombo.SelectedItem as string ?? "Все группы";

            var groups = new List<string> { "Все группы" };
            groups.AddRange(_config.Accounts
                .Select(a => a.Group)
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g));

            GroupFilterCombo.ItemsSource = groups;
            GroupFilterCombo.SelectedItem = groups.Contains(currentSelection) ? currentSelection : "Все группы";
        }

        private void ApplyFilters()
        {
            string selectedGroup = GroupFilterCombo.SelectedItem as string ?? "Все группы";
            string search = (SearchBox.Text ?? string.Empty).Trim();

            _filteredAccounts.Clear();

            foreach (var acc in _config.Accounts)
            {
                bool groupMatch = selectedGroup == "Все группы" ||
                                  string.Equals(acc.Group, selectedGroup, StringComparison.OrdinalIgnoreCase);

                bool searchMatch = string.IsNullOrEmpty(search) ||
                                   acc.Username.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                   acc.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                   acc.Note.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                   acc.Group.Contains(search, StringComparison.OrdinalIgnoreCase);

                if (groupMatch && searchMatch)
                {
                    _filteredAccounts.Add(acc);
                }
            }

            EmptyStatePanel.Visibility = _config.Accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateSelectionSummary();
        }

        private void UpdateSelectionSummary()
        {
            int selectedCount = _config.Accounts.Count(account => account.IsSelected);
            SelectionSummaryText.Text = $"Выбрано: {selectedCount} / {_config.Accounts.Count}";
        }

        private void AddOrUpdateAccountFromLogin(string rawCookie, RobloxUserInfo userInfo, string group = "Основные", string note = "")
        {
            string encrypted = AccountStorageService.EncryptCookie(RobloxApiService.CleanCookie(rawCookie));
            var existing = _config.Accounts.FirstOrDefault(a => a.UserId == userInfo.Id ||
                string.Equals(a.Username, userInfo.Name, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.UserId = userInfo.Id;
                existing.Username = userInfo.Name;
                existing.DisplayName = userInfo.DisplayName;
                existing.AvatarUrl = userInfo.AvatarUrl;
                existing.EncryptedCookie = encrypted;
                existing.StatusText = "Сессия обновлена";
                existing.StatusColor = "#4CAF50";
                if (!string.IsNullOrWhiteSpace(note)) existing.Note = note;
                if (!string.IsNullOrWhiteSpace(group)) existing.Group = group;
                FooterStatusText.Text = $"Аккаунт @{userInfo.Name} успешно обновлен!";
            }
            else
            {
                var newAcc = new RobloxAccount
                {
                    UserId = userInfo.Id,
                    Username = userInfo.Name,
                    DisplayName = userInfo.DisplayName,
                    AvatarUrl = userInfo.AvatarUrl,
                    Group = string.IsNullOrWhiteSpace(group) ? "Основные" : group,
                    Note = note,
                    EncryptedCookie = encrypted,
                    StatusText = "Готов к запуску",
                    StatusColor = "#4CAF50",
                    LastUsed = DateTime.Now
                };
                _config.Accounts.Add(newAcc);
                FooterStatusText.Text = $"Добавлен аккаунт @{userInfo.Name} ({userInfo.DisplayName})!";
            }

            SaveCurrentConfig();
            RefreshGroupFilterCombo();
            ApplyFilters();
        }

        // Добавление через встроенный браузер WebView2
        private void AddViaBrowser_Click(object sender, RoutedEventArgs e)
        {
            var browserWin = new BrowserWindow
            {
                Owner = this
            };

            if (browserWin.ShowDialog() == true &&
                !string.IsNullOrEmpty(browserWin.CapturedCookie) &&
                browserWin.CapturedUserInfo != null)
            {
                AddOrUpdateAccountFromLogin(browserWin.CapturedCookie, browserWin.CapturedUserInfo);
            }
        }

        // Добавление через вставку куки .ROBLOSECURITY
        private async void AddViaCookie_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new AccountEditDialog(requireCookie: true, title: "Добавить аккаунт по .ROBLOSECURITY")
            {
                Owner = this
            };

            if (dlg.ShowDialog() == true)
            {
                FooterStatusText.Text = "Проверка токена .ROBLOSECURITY...";
                var userInfo = await _apiService.ValidateAndGetUserAsync(dlg.EnteredCookie);
                if (userInfo == null)
                {
                    MessageBox.Show(
                        "Токен .ROBLOSECURITY недействителен или устарел. Проверьте скопированную строку или войдите через встроенный браузер.",
                        "Ошибка авторизации",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    FooterStatusText.Text = "Ошибка проверки куки.";
                    return;
                }

                AddOrUpdateAccountFromLogin(dlg.EnteredCookie, userInfo, dlg.SelectedGroup, dlg.EnteredNote);
            }
        }

        // Запуск одного аккаунта в 1 клик
        private async void PlaySingleAccount_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is RobloxAccount account)
            {
                await LaunchAccountInternalAsync(account);
            }
        }

        /// <summary>
        /// Ожидает новый лог клиента и подтверждение входа. Стартовый процесс может завершиться с кодом 0,
        /// передав запуск другому процессу Roblox, поэтому одного статуса Process недостаточно.
        /// </summary>
        private async Task<bool> WaitUntilInstanceReadyAsync(
            RobloxAccount account,
            Process? proc,
            string cleanCookie,
            long browserTrackerId,
            DateTime launchUtc,
            HashSet<string> existingLogPaths,
            string? nextUsername = null)
        {
            if (proc == null)
            {
                account.StatusText = "Не удалось отследить запуск";
                account.StatusColor = "#EF4444";
                return false;
            }

            string logsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Roblox",
                "logs");

            int freshLogCount = 0;
            int? launchProcessExitCode = null;
            bool clientStartupConfirmed = false;

            var sw = Stopwatch.StartNew();

            int launchTimeoutSec = Math.Clamp(_config.RobloxSettings?.LaunchTimeoutSeconds is > 0 ? _config.RobloxSettings.LaunchTimeoutSeconds : 210, 30, 900);
            var lastSingletonCheck = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < launchTimeoutSec)
            {
                try
                {
                    bool launchProcessExited = proc.HasExited;
                    if (launchProcessExited && launchProcessExitCode == null)
                    {
                        try
                        {
                            launchProcessExitCode = proc.ExitCode;
                        }
                        catch (InvalidOperationException)
                        {
                            launchProcessExitCode = -1;
                        }
                    }

                    long memMb = 0;
                    if (!launchProcessExited)
                    {
                        proc.Refresh();
                        memMb = proc.WorkingSet64 / (1024 * 1024);
                    }

                    if (lastSingletonCheck.ElapsedMilliseconds >= 900)
                    {
                        await Task.Run(() => _multiRobloxService.PrepareForNewInstanceLaunch());
                        lastSingletonCheck.Restart();
                    }

                    if (!clientStartupConfirmed &&
                        !RobloxApiService.WriteRobloxCookiesDat(cleanCookie, account.UserId, browserTrackerId))
                    {
                        account.StatusText = "Не удалось удержать сессию выбранного аккаунта перед запуском";
                        account.StatusColor = "#EF4444";
                        return false;
                    }

                    freshLogCount = 0;
                    if (Directory.Exists(logsDir))
                    {
                        var recentLogs = new DirectoryInfo(logsDir)
                            .GetFiles("*_Player_*_last.log")
                            .Where(f => !f.Name.Contains("CrashHandler", StringComparison.OrdinalIgnoreCase) &&
                                        f.LastWriteTimeUtc >= launchUtc &&
                                        !existingLogPaths.Contains(f.FullName))
                            .OrderByDescending(f => f.LastWriteTimeUtc)
                            .Take(16);

                        foreach (var file in recentLogs)
                        {
                            try
                            {
                                using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                                using var sr = new StreamReader(fs);
                                string text = await sr.ReadToEndAsync();
                                freshLogCount++;



                                // A launch can create a short-lived deeplink log before the real client log.
                                // Inspect every fresh log instead of pinning readiness to the first one.
                                if (text.Contains("userDidLogin", StringComparison.Ordinal) ||
                                    text.Contains("RenderView created", StringComparison.Ordinal) ||
                                    text.Contains("Joining game", StringComparison.Ordinal) ||
                                    text.Contains("Roblox stage ReadyForFlagFetch completed", StringComparison.Ordinal) ||
                                    text.Contains("Successfully launched new process in systray mode", StringComparison.Ordinal) ||
                                    text.Contains("SingleSurfaceApp", StringComparison.Ordinal) ||
                                    text.Contains("DUAR is on", StringComparison.Ordinal) ||
                                    text.Contains("Starting module: GlobalInstance", StringComparison.Ordinal))
                                {
                                    clientStartupConfirmed = true;
                                                                        account.StatusText = $"Клиент запущен ({memMb} МБ)";
                                    account.StatusColor = "#4CAF50";
                                    return true;
                                }
                            }
                            catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                        }
                    }

                    if (memMb >= 90)
                    {
                        clientStartupConfirmed = true;
                                                account.StatusText = $"Клиент запущен ({memMb} МБ)";
                        account.StatusColor = "#4CAF50";
                        return true;
                    }

                    string stage = freshLogCount > 0
                        ? "подтверждение входа"
                        : launchProcessExited
                            ? "ожидание клиента Roblox"
                            : "распаковка";
                    string nextInfo = !string.IsNullOrEmpty(nextUsername) ? $" -> след. @{nextUsername}" : "";
                    string memoryInfo = memMb > 0 ? $" ({memMb} МБ)" : string.Empty;
                    account.StatusText = $"Запуск: {stage}{memoryInfo}...";
                    FooterStatusText.Text = $"@{account.Username}: {stage}{memoryInfo}{nextInfo}...";
                }
                catch (Exception ex)
                {
                    account.StatusText = $"Ошибка проверки запуска: {ex.Message}";
                    account.StatusColor = "#EF4444";
                    return false;
                }

                await Task.Delay(350);
            }

            account.StatusText = "Запуск не подтверждён: таймаут";
            account.StatusColor = "#EF4444";
            return false;
        }

        private async Task<bool> LaunchAccountInternalAsync(
            RobloxAccount account,
            string? nextAccountUsername = null,
            string? placeInputOverride = null,
            string? targetInputOverride = null)
        {
            SaveCurrentConfig();

            if (!_multiRobloxService.IsEnabled && MultiRobloxToggle.IsChecked == true)
            {
                _multiRobloxService.Enable();
            }

            account.StatusText = "В очереди на запуск...";
            account.StatusColor = "#FBBF24";

            await _launchLock.WaitAsync();
            try
            {
                // Если предыдущее окно еще загружается (например, нажали кнопки запуска вручную подряд),
                // дожидаемся пока оно откроет 3D-окно (> 260 МБ), чтобы они не подрались за RobloxCookies.dat
                if (_activeBootingProcess != null)
                {
                    var waitSw = Stopwatch.StartNew();
                    while (waitSw.Elapsed.TotalSeconds < 90.0)
                    {
                        try
                        {
                            if (_activeBootingProcess.HasExited) break;
                            _activeBootingProcess.Refresh();
                            long mb = _activeBootingProcess.WorkingSet64 / (1024 * 1024);
                            if (mb >= 260)
                            {
                                await Task.Delay(1200);
                                break;
                            }
                            account.StatusText = $"Ждем пред. окно ({mb} МБ)...";
                            FooterStatusText.Text = $"Ожидание открытия предыдущего окна ({mb} МБ) перед стартом @{account.Username}...";
                        }
                        catch { break; }

                        await Task.Run(() => _multiRobloxService.PrepareForNewInstanceLaunch());
                        await Task.Delay(450);
                    }
                }

                // Принудительно закрываем ROBLOX_singletonEvent у всех уже открытых окон перед запуском нового
                await Task.Run(() => _multiRobloxService.PrepareForNewInstanceLaunch());

                _lastRobloxSettingsNotice = TryApplyRobloxSettingsBeforeLaunch();

                account.StatusText = "Получение тикета и запуск...";
                account.StatusColor = "#FBBF24";
                FooterStatusText.Text = $"Запуск игры для @{account.Username}...";

                DateTime launchUtc = DateTime.UtcNow;
                HashSet<string> existingLogPaths;
                try
                {
                    existingLogPaths = GetCurrentRobloxLogPaths();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    account.StatusText = $"Не удалось проверить логи Roblox: {ex.Message}";
                    account.StatusColor = "#EF4444";
                    FooterStatusText.Text = $"@{account.Username}: {account.StatusText}";
                    return false;
                }

                string launchPlaceInput = placeInputOverride ?? PlaceInputTextBox.Text;
                string launchTargetInput = targetInputOverride ?? VipOrJobTextBox.Text;
                HashSet<int> pidsBeforeLaunch = GetRobloxPids();
                _lastConfirmedLogPath = null;

                var (success, message, startedProc, cleanCookie, trackerId) =
                    await _apiService.LaunchAccountAsync(account, launchPlaceInput, launchTargetInput);

                account.StatusText = message;
                account.StatusColor = success ? "#FBBF24" : "#EF4444";
                FooterStatusText.Text = $"@{account.Username}: {message}";
                SaveCurrentConfig();

                if (!success)
                {
                    return false;
                }

                if (startedProc == null)
                {
                    account.StatusText = "Запуск не подтверждён: процесс не отслеживается";
                    account.StatusColor = "#EF4444";
                    FooterStatusText.Text = $"@{account.Username}: {account.StatusText}";
                    return false;
                }

                _activeBootingProcess = startedProc;
                _lastLaunchTime = DateTime.Now;

                bool ready = await WaitUntilInstanceReadyAsync(
                    account,
                    startedProc,
                    cleanCookie,
                    trackerId,
                    launchUtc,
                    existingLogPaths,
                    nextAccountUsername);
                if (ready)
                {
                    await RegisterSessionAsync(account, trackerId, startedProc, launchPlaceInput, launchTargetInput, pidsBeforeLaunch, existingLogPaths, launchUtc);
                }
                FooterStatusText.Text = ready
                    ? $"@{account.Username}: запуск подтверждён.{FormatSettingsNotice(_lastRobloxSettingsNotice)}"
                    : $"@{account.Username}: {account.StatusText}{FormatSettingsNotice(_lastRobloxSettingsNotice)}";
                return ready;
            }
            finally
            {
                _launchLock.Release();
            }
        }

        // ===== Отслеживание дисконнектов и авто-переподключение =====

        private sealed class TrackedSession
        {
            public required RobloxAccount Account { get; init; }
            public required long BrowserTrackerId { get; init; }
            public string PlaceInput { get; set; } = string.Empty;
            public string TargetInput { get; set; } = string.Empty;
            public string? LogPath { get; set; }
            public long LogOffset { get; set; }
            public int? InitialPid { get; set; }
            public int? Pid { get; set; }
            public DateTime LaunchTime { get; init; } = DateTime.UtcNow;
            public DateTime? DisconnectDetectedUtc { get; set; }
            public DateTime? TeleportStartedUtc { get; set; }
            public string? DisconnectReason { get; set; }
        }

        // Маркеры реального обрыва связи / кика (НЕ включают штатный код 285 при авто-телепорте и перезапуске окна!)
        private static readonly string[] TrueDisconnectMarkers =
        {
            "reason: 277",
            "Reason: 277",
            "Error Code: 277",
            "reason: 279",
            "Reason: 279",
            "Error Code: 279",
            "reason: 276",
            "Reason: 276",
            "reason: 275",
            "Reason: 275",
            "reason: 274",
            "Reason: 274",
            "reason: 273",
            "Reason: 273",
            "Error Code: 273",
            "reason: 272",
            "Reason: 272",
            "reason: 271",
            "Reason: 271",
            "Error Code: 271",
            "reason: 268",
            "Reason: 268",
            "Error Code: 268",
            "reason: 267",
            "Reason: 267",
            "Error Code: 267",
            "reason: 266",
            "Reason: 266",
            "reason: 264",
            "Reason: 264",
            "reason: 262",
            "Reason: 262",
            "reason: 261",
            "Reason: 261",
            "reason: 260",
            "Reason: 260",
            "ID_CONNECTION_LOST",
            "Connection lost: AckTimeout 1",
            "Lost connection with reason: 277",
            "Lost connection with reason: 279",
            "Lost connection with reason: 267",
            "Lost connection with reason: 268",
            "Lost connection with reason: 273",
        };

        // Маркеры внутриигрового авто-переподключения (телепорта AFK-фарма) или успешного входа на сервер
        private static readonly string[] TeleportOrJoinMarkers =
        {
            "doTeleport",
            "Teleported.",
            "! Joining game",
            "Connection accepted from",
            "Replicator created for player",
            "Report game_join_loadtime:",
        };

        private static bool IsTeleportOrJoinLogChunk(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (string marker in TeleportOrJoinMarkers)
            {
                if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool IsTrueDisconnectLogChunk(string text, out string? matchedMarker)
        {
            matchedMarker = null;
            if (string.IsNullOrEmpty(text)) return false;

            string[] lines = text.Split('\n');
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;

                // Игнорируем фоновый тестовый транспорт и штатные клиентские переходы/телепорты (Reason 285 / Reason 1)
                if (line.Contains("RbxTransportDummyClient", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("reason: 285", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Player: 285", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("DisconnectClientInitiated", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Disconnect ASAP", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Peer Disconnected", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("reason: 1 ", StringComparison.OrdinalIgnoreCase) ||
                    line.EndsWith("reason: 1", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (string marker in TrueDisconnectMarkers)
                {
                    if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedMarker = marker;
                        return true;
                    }
                }
            }

            return false;
        }

        private const int MaxReconnectsPerWindow = 15;
        private static readonly TimeSpan ReconnectWindow = TimeSpan.FromMinutes(15);

        private readonly List<TrackedSession> _sessions = new();
        private readonly Dictionary<long, List<DateTime>> _reconnectHistory = new();
        private readonly DispatcherTimer _disconnectMonitorTimer = new() { Interval = TimeSpan.FromSeconds(2) };
        private bool _disconnectMonitorBusy;
        private string? _lastConfirmedLogPath;

        private static HashSet<int> GetRobloxPids()
        {
            var result = new HashSet<int>();
            try
            {
                foreach (Process p in Process.GetProcessesByName("RobloxPlayerBeta"))
                {
                    result.Add(p.Id);
                    p.Dispose();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return result;
        }

        private static int? FindProcessId(long trackerId, int? initialPid)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, ParentProcessId, CommandLine FROM Win32_Process WHERE Name = 'RobloxPlayerBeta.exe'");
                var list = new List<(int pid, int ppid, string cmd)>();
                foreach (ManagementObject obj in searcher.Get())
                {
                    int pid = int.TryParse(obj["ProcessId"]?.ToString(), out int p) ? p : 0;
                    int ppid = int.TryParse(obj["ParentProcessId"]?.ToString(), out int pp) ? pp : 0;
                    string cmd = obj["CommandLine"]?.ToString() ?? string.Empty;
                    list.Add((pid, ppid, cmd));
                }

                // 1. Ищем процесс с явным browsertrackerid в командной строке
                string trackerStr = $"browsertrackerid:{trackerId}";
                var direct = list.FirstOrDefault(x => x.cmd.Contains(trackerStr, StringComparison.OrdinalIgnoreCase));
                if (direct.pid != 0)
                {
                    // Если у него есть дочерний процесс (systray-режим), то дочерний процесс и есть активное игровое окно!
                    var child = list.FirstOrDefault(x => x.ppid == direct.pid);
                    return child.pid != 0 ? child.pid : direct.pid;
                }

                // 2. Если исходный PID известен, ищем его дочерний процесс
                if (initialPid.HasValue)
                {
                    var child = list.FirstOrDefault(x => x.ppid == initialPid.Value);
                    if (child.pid != 0) return child.pid;
                }

                // 3. Если запущен только один RobloxPlayerBeta, возвращаем его
                if (list.Count == 1)
                {
                    return list[0].pid;
                }
            }
            catch { }
            return null;
        }

        private static string? FindPlayerLog(long trackerId, long? userId, DateTime sinceUtc, HashSet<string>? excludedLogPaths = null)
        {
            try
            {
                string logsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");
                if (!Directory.Exists(logsDir)) return null;

                string trackerStr = trackerId.ToString();
                var files = new DirectoryInfo(logsDir).GetFiles("*_Player_*_last.log")
                    .Where(f => !f.Name.Contains("CrashHandler", StringComparison.OrdinalIgnoreCase) &&
                                f.LastWriteTimeUtc >= sinceUtc.AddSeconds(-30) &&
                                (excludedLogPaths == null || !excludedLogPaths.Contains(f.FullName)))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();

                // 1. Точное совпадение по userid:<userId> из строки Report game_join_loadtime
                if (userId.HasValue)
                {
                    string exactUserToken = $"userid:{userId.Value}";
                    string ticketUserToken = $"\"UserId\"%3a{userId.Value}";
                    foreach (var file in files)
                    {
                        try
                        {
                            using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
                            char[] buffer = new char[65536];
                            int read = sr.Read(buffer, 0, buffer.Length);
                            string head = new string(buffer, 0, read);
                            if (head.Contains(exactUserToken, StringComparison.OrdinalIgnoreCase) ||
                                head.Contains(ticketUserToken, StringComparison.OrdinalIgnoreCase))
                            {
                                return file.FullName;
                            }
                        }
                        catch { }
                    }
                }

                // 2. Совпадение по browsertrackerid
                foreach (var file in files)
                {
                    try
                    {
                        using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
                        for (int i = 0; i < 60; i++)
                        {
                            string? line = sr.ReadLine();
                            if (line == null) break;
                            if (trackerId > 0 && line.Contains(trackerStr, StringComparison.Ordinal))
                                return file.FullName;
                            if (userId.HasValue && (line.Contains($"rbxuid={userId.Value}") || line.Contains($"rbxid={userId.Value}")))
                                return file.FullName;
                        }
                    }
                    catch { }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private void AttachToExistingRobloxSessions()
        {
            try
            {
                string logsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");
                if (!Directory.Exists(logsDir)) return;

                // Собираем запущенные игровые процессы RobloxPlayerBeta, отсортированные по времени старта
                var runningProcs = new List<(int Pid, DateTime StartUtc)>();
                foreach (Process p in Process.GetProcessesByName("RobloxPlayerBeta"))
                {
                    try
                    {
                        if (!p.HasExited && (p.WorkingSet64 > 50L * 1024 * 1024 || !string.IsNullOrEmpty(p.MainWindowTitle)))
                        {
                            DateTime startUtc = DateTime.UtcNow.AddMinutes(-10);
                            try { startUtc = p.StartTime.ToUniversalTime(); } catch { }
                            runningProcs.Add((p.Id, startUtc));
                        }
                    }
                    catch { }
                    finally
                    {
                        p.Dispose();
                    }
                }

                if (runningProcs.Count == 0) return;
                runningProcs.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));

                var usedPids = _sessions.Where(s => s.Pid.HasValue).Select(s => s.Pid!.Value).ToHashSet();
                var usedLogs = _sessions.Where(s => s.LogPath != null).Select(s => s.LogPath!).ToHashSet(StringComparer.OrdinalIgnoreCase);

                var candidateLogs = new DirectoryInfo(logsDir)
                    .GetFiles("*_Player_*_last.log")
                    .Where(f => !f.Name.Contains("CrashHandler", StringComparison.OrdinalIgnoreCase) &&
                                !usedLogs.Contains(f.FullName) &&
                                (DateTime.UtcNow - f.LastWriteTimeUtc).TotalHours < 12.0 &&
                                f.Length > 2048)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(Math.Max(runningProcs.Count + 4, 10))
                    .ToList();

                var parsedLogs = new List<(FileInfo File, long UserId, string? PlaceId, long BrowserTrackerId, DateTime CreatedUtc)>();
                foreach (var file in candidateLogs)
                {
                    try
                    {
                        using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        int headLen = (int)Math.Min(131072, fs.Length);
                        byte[] buf = new byte[headLen];
                        int bytesRead = fs.Read(buf, 0, headLen);
                        string head = System.Text.Encoding.UTF8.GetString(buf, 0, bytesRead);

                        long userId = 0;
                        string? placeId = null;
                        long btid = 0;

                        // 1. Самый точный источник — Report game_join_loadtime: placeid:..., userid:...
                        var joinMatch = System.Text.RegularExpressions.Regex.Match(
                            head,
                            @"Report game_join_loadtime:\s*placeid:(\d+).*?userid:(\d+)",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (joinMatch.Success)
                        {
                            placeId = joinMatch.Groups[1].Value;
                            long.TryParse(joinMatch.Groups[2].Value, out userId);
                        }

                        // 2. Резервный источник — ticket={"UserId"%3a12345
                        if (userId == 0)
                        {
                            var ticketMatch = System.Text.RegularExpressions.Regex.Match(
                                head,
                                @"""UserId""(?:%3a|:)(\d+)",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (ticketMatch.Success)
                                long.TryParse(ticketMatch.Groups[1].Value, out userId);
                        }

                        // 3. Резервный источник — Players.<Username>.PlayerGui
                        if (userId == 0)
                        {
                            foreach (var acc in _config.Accounts)
                            {
                                if (!string.IsNullOrWhiteSpace(acc.Username) &&
                                    head.Contains($"Players.{acc.Username}.", StringComparison.OrdinalIgnoreCase))
                                {
                                    userId = acc.UserId;
                                    break;
                                }
                            }
                        }

                        // 4. Резервный источник — rbxuid=
                        if (userId == 0)
                        {
                            var rbxuidMatch = System.Text.RegularExpressions.Regex.Match(head, @"rbxuid=(\d+)");
                            if (rbxuidMatch.Success)
                                long.TryParse(rbxuidMatch.Groups[1].Value, out userId);
                        }

                        if (string.IsNullOrEmpty(placeId))
                        {
                            var placeMatch = System.Text.RegularExpressions.Regex.Match(head, @"place (\d+) at");
                            if (placeMatch.Success)
                                placeId = placeMatch.Groups[1].Value;
                        }

                        var btidMatches = System.Text.RegularExpressions.Regex.Matches(head, @"browserid=(\d+)");
                        if (btidMatches.Count > 0)
                        {
                            long.TryParse(btidMatches[^1].Groups[1].Value, out btid);
                        }

                        if (userId != 0)
                        {
                            DateTime createdUtc = file.CreationTimeUtc;
                            var tsMatch = System.Text.RegularExpressions.Regex.Match(file.Name, @"_(\d{8}T\d{6}Z)_Player_");
                            if (tsMatch.Success &&
                                DateTime.TryParseExact(
                                    tsMatch.Groups[1].Value,
                                    "yyyyMMdd'T'HHmmss'Z'",
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                                    out DateTime parsedTs))
                            {
                                createdUtc = parsedTs;
                            }

                            parsedLogs.Add((file, userId, placeId, btid, createdUtc));
                        }
                    }
                    catch { }
                }

                var uniqueAccountLogs = parsedLogs
                    .GroupBy(x => x.UserId)
                    .Select(g => g.OrderByDescending(x => x.File.LastWriteTimeUtc).First())
                    .OrderByDescending(x => x.File.LastWriteTimeUtc)
                    .Take(runningProcs.Count)
                    .OrderBy(x => x.CreatedUtc)
                    .ToList();

                foreach (var entry in uniqueAccountLogs)
                {
                    var account = _config.Accounts.FirstOrDefault(a => a.UserId == entry.UserId);
                    if (account == null) continue;
                    if (_sessions.Any(s => s.Account.UserId == account.UserId)) continue;

                    int? matchedPid = FindProcessId(entry.BrowserTrackerId, null);
                    if (matchedPid == null || usedPids.Contains(matchedPid.Value))
                    {
                        var availableProcs = runningProcs.Where(p => !usedPids.Contains(p.Pid)).ToList();
                        if (availableProcs.Count > 0)
                        {
                            var closest = availableProcs
                                .OrderBy(p => Math.Abs((p.StartUtc - entry.CreatedUtc).TotalSeconds))
                                .First();
                            matchedPid = closest.Pid;
                        }
                    }

                    if (matchedPid.HasValue)
                    {
                        usedPids.Add(matchedPid.Value);
                    }
                    usedLogs.Add(entry.File.FullName);

                    string effectivePlace = !string.IsNullOrWhiteSpace(PlaceInputTextBox.Text)
                        ? PlaceInputTextBox.Text.Trim()
                        : (entry.PlaceId ?? string.Empty);
                    string effectiveTarget = VipOrJobTextBox.Text.Trim();

                    long offset = 0;
                    try { offset = entry.File.Length; } catch { }

                    _sessions.Add(new TrackedSession
                    {
                        Account = account,
                        BrowserTrackerId = entry.BrowserTrackerId,
                        PlaceInput = effectivePlace,
                        TargetInput = effectiveTarget,
                        LogPath = entry.File.FullName,
                        LogOffset = offset,
                        InitialPid = matchedPid,
                        Pid = matchedPid,
                        LaunchTime = entry.CreatedUtc
                    });

                    account.StatusText = "В игре";
                    account.StatusColor = "#4CAF50";
                }
            }
            catch { }
        }

        private static int? FindGameProcessId(HashSet<int>? excludedPids = null)
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName("RobloxPlayerBeta"))
                {
                    try
                    {
                        int id = p.Id;
                        if (excludedPids != null && excludedPids.Contains(id))
                            continue;

                        if (!string.IsNullOrEmpty(p.MainWindowTitle) || p.WorkingSet64 > 80L * 1024 * 1024)
                        {
                            return id;
                        }
                    }
                    catch { }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch { }
            return null;
        }

        private async Task RegisterSessionAsync(
            RobloxAccount account,
            long browserTrackerId,
            Process? initialProcess,
            string placeInput,
            string targetInput,
            HashSet<int> pidsBeforeLaunch,
            HashSet<string> existingLogPaths,
            DateTime launchUtc)
        {
            _sessions.RemoveAll(s => s.Account.UserId == account.UserId);

            int? initialPid = null;
            try
            {
                if (initialProcess != null && !initialProcess.HasExited)
                    initialPid = initialProcess.Id;
            }
            catch { }

            int? pid = FindProcessId(browserTrackerId, initialPid);
            if (pid == null)
            {
                var trackedPids = _sessions.Where(s => s.Pid.HasValue).Select(s => s.Pid!.Value).ToHashSet();
                for (int attempt = 0; attempt < 8 && pid == null; attempt++)
                {
                    pid = FindProcessId(browserTrackerId, initialPid) ?? FindGameProcessId(trackedPids);
                    if (pid == null) await Task.Delay(350);
                }
            }

            string? logPath = _lastConfirmedLogPath;
            var trackedLogs = _sessions.Where(s => s.LogPath != null).Select(s => s.LogPath!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (logPath == null || trackedLogs.Contains(logPath))
            {
                logPath = FindPlayerLog(browserTrackerId, account.UserId, launchUtc, trackedLogs);
            }

            long offset = 0;
            if (logPath != null)
            {
                try { offset = new FileInfo(logPath).Length; } catch (IOException) { }
            }

            _sessions.Add(new TrackedSession
            {
                Account = account,
                BrowserTrackerId = browserTrackerId,
                PlaceInput = placeInput,
                TargetInput = targetInput,
                LogPath = logPath,
                LogOffset = offset,
                InitialPid = initialPid,
                Pid = pid ?? initialPid,
                LaunchTime = DateTime.UtcNow
            });
        }

        private static string ReadNewLogText(TrackedSession session)
        {
            if (session.LogPath == null) return string.Empty;
            try
            {
                using var fs = new FileStream(session.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < session.LogOffset) session.LogOffset = 0;
                if (fs.Length == session.LogOffset) return string.Empty;
                fs.Seek(session.LogOffset, SeekOrigin.Begin);
                using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
                string text = sr.ReadToEnd();
                session.LogOffset = fs.Length;
                return text;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }

        private static bool IsProcessAlive(int? pid)
        {
            if (pid == null) return false;
            try
            {
                using Process p = Process.GetProcessById(pid.Value);
                return !p.HasExited;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        private void KillSessionGameProcess(TrackedSession session)
        {
            if (session.Pid.HasValue)
            {
                try
                {
                    using Process p = Process.GetProcessById(session.Pid.Value);
                    if (!p.HasExited)
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                        return;
                    }
                }
                catch { }
            }

            int? pid = FindProcessId(session.BrowserTrackerId, session.InitialPid);
            if (pid.HasValue)
            {
                try
                {
                    using Process p = Process.GetProcessById(pid.Value);
                    if (!p.HasExited)
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                        return;
                    }
                }
                catch { }
            }

            var otherTrackedPids = _sessions.Where(s => s != session && s.Pid.HasValue).Select(s => s.Pid!.Value).ToHashSet();
            try
            {
                var allProcs = Process.GetProcessesByName("RobloxPlayerBeta");
                // Только если осталось одно неподконтрольное окно (или всего одно окно), закрываем его.
                // Никогда не закрываем веером все процессы, если открыто несколько окон!
                var untracked = allProcs.Where(p => !otherTrackedPids.Contains(p.Id)).ToList();
                if (untracked.Count == 1)
                {
                    try
                    {
                        untracked[0].Kill();
                        untracked[0].WaitForExit(2500);
                    }
                    catch { }
                }
                foreach (Process p in allProcs)
                {
                    try { p.Dispose(); } catch { }
                }
            }
            catch { }
        }

        private async void DisconnectMonitorTimer_Tick(object? sender, EventArgs e)
        {
            if (_disconnectMonitorBusy) return;
            _disconnectMonitorBusy = true;
            try
            {
                int totalRobloxProcs = Process.GetProcessesByName("RobloxPlayerBeta").Length;
                if (totalRobloxProcs > _sessions.Count)
                {
                    AttachToExistingRobloxSessions();
                }

                if (_sessions.Count == 0) return;

                foreach (TrackedSession session in _sessions.ToList())
                {
                    // 1. Поддерживаем актуальный PID игрового окна
                    if (session.Pid == null || !IsProcessAlive(session.Pid))
                    {
                        session.Pid = FindProcessId(session.BrowserTrackerId, session.InitialPid);
                        if (session.Pid == null)
                        {
                            var otherTrackedPids = _sessions.Where(s => s != session && s.Pid.HasValue).Select(s => s.Pid!.Value).ToHashSet();
                            session.Pid = FindGameProcessId(otherTrackedPids);
                        }
                    }

                    // 2. Проверяем, не появился ли более свежий активный лог этого сеанса
                    var otherTrackedLogs = _sessions
                        .Where(s => s != session && s.LogPath != null)
                        .Select(s => s.LogPath!)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    string? latestLog = FindPlayerLog(session.BrowserTrackerId, session.Account.UserId, session.LaunchTime, otherTrackedLogs);
                    if (latestLog != null && !string.Equals(latestLog, session.LogPath, StringComparison.OrdinalIgnoreCase))
                    {
                        session.LogPath = latestLog;
                        try { session.LogOffset = new FileInfo(latestLog).Length; } catch { session.LogOffset = 0; }
                    }

                    double graceSeconds = Math.Clamp(_config.RobloxSettings?.StartupGracePeriodSeconds is > 0 ? _config.RobloxSettings.StartupGracePeriodSeconds : 180, 15, 900);
                    double teleportProtSeconds = Math.Clamp(_config.RobloxSettings?.TeleportProtectionSeconds is > 0 ? _config.RobloxSettings.TeleportProtectionSeconds : 150, 15, 900);
                    double disconnectConfirmSeconds = Math.Clamp(_config.RobloxSettings?.DisconnectConfirmSeconds is > 0 ? _config.RobloxSettings.DisconnectConfirmSeconds : 20, 3, 300);

                    bool inGracePeriod = (DateTime.UtcNow - session.LaunchTime).TotalSeconds < graceSeconds;

                    // 3. Читаем новые записи лога и сдвигаем смещение
                    string newText = ReadNewLogText(session);

                    if (!string.IsNullOrEmpty(newText))
                    {
                        // Обновляем последний известный PlaceId из лога, если поле PlaceInput пустое
                        if (string.IsNullOrWhiteSpace(session.PlaceInput))
                        {
                            var pm = System.Text.RegularExpressions.Regex.Match(newText, @"place (\d+) at|placeid:(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (pm.Success)
                            {
                                string foundPlace = !string.IsNullOrEmpty(pm.Groups[1].Value) ? pm.Groups[1].Value : pm.Groups[2].Value;
                                if (!string.IsNullOrEmpty(foundPlace))
                                    session.PlaceInput = foundPlace;
                            }
                        }

                        // Если начался внутриигровой телепорт или переход (Reason 285 / doTeleport / Teleported),
                        // включаем защиту на время переподключения внутри игры
                        if (newText.Contains("doTeleport", StringComparison.OrdinalIgnoreCase) ||
                            newText.Contains("Teleported.", StringComparison.OrdinalIgnoreCase) ||
                            newText.Contains("reason: 285", StringComparison.OrdinalIgnoreCase) ||
                            newText.Contains("DisconnectClientInitiated", StringComparison.OrdinalIgnoreCase))
                        {
                            session.TeleportStartedUtc = DateTime.UtcNow;
                            session.DisconnectDetectedUtc = null;
                            session.DisconnectReason = null;
                        }

                        // Когда соединение с новым сервером установлено, снимаем флаг телепорта
                        if (newText.Contains("Connection accepted from", StringComparison.OrdinalIgnoreCase) ||
                            newText.Contains("Replicator created for player", StringComparison.OrdinalIgnoreCase) ||
                            newText.Contains("Report game_join_loadtime:", StringComparison.OrdinalIgnoreCase))
                        {
                            session.TeleportStartedUtc = null;
                            session.DisconnectDetectedUtc = null;
                            session.DisconnectReason = null;
                        }

                        bool inTeleportCooldown = session.TeleportStartedUtc.HasValue &&
                                                  (DateTime.UtcNow - session.TeleportStartedUtc.Value).TotalSeconds < teleportProtSeconds;

                        // Если в логе идет штатный внутриигровой телепорт / авто-реконнект самой игры (AFK-фарм),
                        // сбрасываем ожидание дисконнекта и ничего не перезапускаем!
                        if (IsTeleportOrJoinLogChunk(newText) || inTeleportCooldown)
                        {
                            session.DisconnectDetectedUtc = null;
                            session.DisconnectReason = null;
                            if (session.Account.StatusText.StartsWith("Дисконнект") ||
                                session.Account.StatusText.StartsWith("Проверка"))
                            {
                                session.Account.StatusText = "В игре";
                                session.Account.StatusColor = "#4CAF50";
                            }
                        }
                        else if (!inGracePeriod && IsTrueDisconnectLogChunk(newText, out string? matchedReason))
                        {
                            session.DisconnectDetectedUtc ??= DateTime.UtcNow;
                            session.DisconnectReason = matchedReason;
                        }
                    }

                    bool isTeleportingNow = session.TeleportStartedUtc.HasValue &&
                                            (DateTime.UtcNow - session.TeleportStartedUtc.Value).TotalSeconds < teleportProtSeconds;

                    // Если зафиксирован реальный код ошибки (277, 279, 267, 268, 273 и т.д.),
                    // ждем заданное время, чтобы убедиться, что это НЕ затянувшийся внутриигровой телепорт.
                    if (!inGracePeriod && !isTeleportingNow && session.DisconnectDetectedUtc.HasValue)
                    {
                        double secondsSinceDisconnect = (DateTime.UtcNow - session.DisconnectDetectedUtc.Value).TotalSeconds;
                        if (secondsSinceDisconnect >= disconnectConfirmSeconds)
                        {
                            RobloxAccount account = session.Account;
                            if (AutoReconnectCheckBox.IsChecked != true)
                            {
                                account.StatusText = "Дисконнект (ожидание авто-переподключения)";
                                account.StatusColor = "#EF4444";
                                FooterStatusText.Text = $"@{account.Username}: обнаружен дисконнект. Включите галочку «Авто-переподключение», чтобы перезайти.";
                            }
                            else
                            {
                                _sessions.Remove(session);
                                if (string.IsNullOrWhiteSpace(session.PlaceInput) && !string.IsNullOrWhiteSpace(PlaceInputTextBox.Text))
                                    session.PlaceInput = PlaceInputTextBox.Text.Trim();
                                if (string.IsNullOrWhiteSpace(session.TargetInput) && !string.IsNullOrWhiteSpace(VipOrJobTextBox.Text))
                                    session.TargetInput = VipOrJobTextBox.Text.Trim();

                                await ReconnectAsync(session);
                                continue;
                            }
                        }
                    }

                    // 4. Проверяем состояние активности окна
                    bool isAlive = session.Pid != null && IsProcessAlive(session.Pid);
                    bool anyRobloxRunning = totalRobloxProcs > 0;

                    bool logActive = false;
                    if (session.LogPath != null && File.Exists(session.LogPath))
                    {
                        try
                        {
                            var fi = new FileInfo(session.LogPath);
                            logActive = (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalSeconds < Math.Max(graceSeconds, 120.0);
                        }
                        catch { }
                    }

                    if (isAlive || (anyRobloxRunning && (logActive || _sessions.Count == 1)) || inGracePeriod || isTeleportingNow)
                    {
                        if (!session.DisconnectDetectedUtc.HasValue &&
                            session.Account.StatusText != "В игре" &&
                            !session.Account.StatusText.StartsWith("Переподключён") &&
                            !session.Account.StatusText.StartsWith("Клиент запущен"))
                        {
                            session.Account.StatusText = "В игре";
                            session.Account.StatusColor = "#4CAF50";
                        }
                    }
                    else if (!anyRobloxRunning || (!isAlive && !logActive && (DateTime.UtcNow - session.LaunchTime).TotalSeconds > graceSeconds + 60.0))
                    {
                        _sessions.Remove(session);
                        session.Account.StatusText = "Окно закрыто";
                        session.Account.StatusColor = "#94A3B8";
                    }
                }
            }
            finally
            {
                _disconnectMonitorBusy = false;
            }
        }

        private async Task ReconnectAsync(TrackedSession session)
        {
            RobloxAccount account = session.Account;
            if (!_reconnectHistory.TryGetValue(account.UserId, out var history))
            {
                history = new List<DateTime>();
                _reconnectHistory[account.UserId] = history;
            }
            history.RemoveAll(t => DateTime.Now - t > ReconnectWindow);

            if (history.Count >= MaxReconnectsPerWindow)
            {
                account.StatusText = $"Дисконнект: лимит {MaxReconnectsPerWindow} переподключений";
                account.StatusColor = "#EF4444";
                FooterStatusText.Text = $"@{account.Username}: {account.StatusText}";
                return;
            }

            account.StatusText = "Дисконнект (Error 277) — перезаход...";
            account.StatusColor = "#FBBF24";
            FooterStatusText.Text = $"@{account.Username}: обнаружен дисконнект (Error 277). Закрываю зависшее окно и перезахожу...";

            // 1. Принудительно закрываем зависшее окно с диалогом ошибки
            await Task.Run(() => KillSessionGameProcess(session));
            await Task.Delay(2500);

            // 2. Освобождаем mutex ROBLOX_singletonEvent перед новым стартом
            await Task.Run(() => _multiRobloxService.PrepareForNewInstanceLaunch());

            // 3. Запускаем аккаунт заново в тот же целевой плейс или к тому же игроку (до 10 попыток)
            for (int attempt = 1; attempt <= 10; attempt++)
            {
                history.Add(DateTime.Now);
                bool ok = await LaunchAccountInternalAsync(account, null, session.PlaceInput, session.TargetInput);
                if (ok)
                {
                    account.StatusText = $"Переподключён ({history.Count}/{MaxReconnectsPerWindow})";
                    account.StatusColor = "#4CAF50";
                    FooterStatusText.Text = $"@{account.Username}: успешно переподключён после дисконнекта!";
                    return;
                }
                if (attempt < 10 && history.Count < MaxReconnectsPerWindow)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(3 * attempt, 18)));
                else
                    break;
            }

            FooterStatusText.Text = $"@{account.Username}: переподключиться не удалось — {account.StatusText}";
        }

        private void AutoReconnectCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            SaveCurrentConfig();
            FooterStatusText.Text = AutoReconnectCheckBox.IsChecked == true
                ? "Авто-переподключение включено: окна, запущенные из менеджера, перезайдут при обрыве связи."
                : "Авто-переподключение выключено (дисконнекты всё равно отображаются в статусе).";
        }

        private void PreventSleepCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            bool enable = PreventSleepCheckBox.IsChecked == true;
            if (enable)
            {
                _powerManagementService.Enable(_config.SavedLidAcValue, _config.SavedLidDcValue);
                FooterStatusText.Text = "Режим «Не уходить в сон» включен: Windows не уснет при бездействии и при закрытии крышки ноутбука.";
            }
            else
            {
                _powerManagementService.Disable();
                FooterStatusText.Text = "Режим «Не уходить в сон» выключен: возвращено стандартное поведение Windows.";
            }

            SaveCurrentConfig();
        }

        private async void AntiAfkCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            bool enable = AntiAfkCheckBox.IsChecked == true;
            SaveCurrentConfig();

            if (enable)
            {
                int intervalMin = Math.Clamp(_config.RobloxSettings?.AntiAfkIntervalMinutes is > 0 ? _config.RobloxSettings.AntiAfkIntervalMinutes : 12, 1, 18);
                _lastAntiAfkPulseUtc = DateTime.UtcNow;
                FooterStatusText.Text = $"Анти-АФК включен (каждые {intervalMin} мин, безопасная клавиша Right Ctrl). Отправляю пробный импульс...";
                int count = await _antiAfkService.SendAntiAfkPulseToAllWindowsAsync();
                if (count > 0)
                {
                    FooterStatusText.Text = $"Анти-АФК включен (интервал: {intervalMin} мин). Сброшен таймер бездействия у {count} окон Roblox.";
                }
            }
            else
            {
                FooterStatusText.Text = "Анти-АФК выключен.";
            }
        }

        private async void AntiAfkTimer_Tick(object? sender, EventArgs e)
        {
            if (AntiAfkCheckBox.IsChecked != true)
                return;

            int intervalMin = Math.Clamp(_config.RobloxSettings?.AntiAfkIntervalMinutes is > 0 ? _config.RobloxSettings.AntiAfkIntervalMinutes : 12, 1, 18);
            double elapsedMinutes = (DateTime.UtcNow - _lastAntiAfkPulseUtc).TotalMinutes;
            if (elapsedMinutes < intervalMin)
                return;

            // Если пользователь прямо сейчас двигает мышь или печатает (< 8 сек назад),
            // откладываем импульс до тех пор, пока он не отпустит мышь/клавиатуру, чтобы не перехватывать фокус под рукой
            if (AntiAfkService.GetUserIdleSeconds() < 8.0 && elapsedMinutes < 18.5)
                return;

            _lastAntiAfkPulseUtc = DateTime.UtcNow;
            int pulsed = await _antiAfkService.SendAntiAfkPulseToAllWindowsAsync();
            if (pulsed > 0)
            {
                FooterStatusText.Text = $"[Анти-АФК {DateTime.Now:HH:mm:ss}] Таймер бездействия успешно сброшен у {pulsed} окон Roblox (Right Ctrl).";
            }
        }


        private static HashSet<string> GetCurrentRobloxLogPaths()
        {
            string logsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Roblox",
                "logs");

            if (!Directory.Exists(logsDir))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return new HashSet<string>(
                Directory.GetFiles(logsDir, "*_Player_*_last.log"),
                StringComparer.OrdinalIgnoreCase);
        }

        private string? TryApplyRobloxSettingsBeforeLaunch()
        {
            if (_config.RobloxSettings is not RobloxGameSettings settings)
                return null;

            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName("RobloxPlayerBeta");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return $"Настройки Roblox не изменены: не удалось проверить процессы ({ex.Message}).";
            }

            bool activeClientFound = false;
            foreach (Process process in processes)
            {
                try
                {
                    // The launcher keeps a small --launch-to-tray process alive even when no game window is open.
                    // It does not read the graphics/FPS settings, so only a real client should block file updates.
                    if (process.MainWindowHandle != IntPtr.Zero ||
                        process.WorkingSet64 > 250L * 1024 * 1024)
                    {
                        activeClientFound = true;
                    }
                }
                catch (Exception ex) when (
                    ex is InvalidOperationException or System.ComponentModel.Win32Exception or
                    UnauthorizedAccessException or NotSupportedException)
                {
                    activeClientFound = true;
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (activeClientFound)
            {
                if (_lastAppliedRobloxSettings is not null &&
                    HaveSameRobloxSettings(_lastAppliedRobloxSettings, settings))
                {
                    return null;
                }

                return "Настройки пропущены: игровое окно Roblox уже открыто. Закройте все игровые окна, чтобы применить новые значения.";
            }

            try
            {
                _robloxSettingsService.Apply(settings);
                _lastAppliedRobloxSettings = new RobloxGameSettings
                {
                    AutomaticGraphics = settings.AutomaticGraphics,
                    GraphicsQualityLevel = settings.GraphicsQualityLevel,
                    MasterVolume = settings.MasterVolume,
                    MouseSensitivity = settings.MouseSensitivity,
                    LowerBackgroundProcessPriority = settings.LowerBackgroundProcessPriority,
                    FrameRateCap = RobloxSettingsService.NormalizeFrameRateCap(settings.FrameRateCap)
                };
                return null;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or XmlException or InvalidDataException)
            {
                return $"Не удалось применить настройки Roblox; запуск продолжен с текущими значениями ({ex.Message}).";
            }
        }

        private static bool HaveSameRobloxSettings(RobloxGameSettings first, RobloxGameSettings second)
        {
            return first.AutomaticGraphics == second.AutomaticGraphics &&
                   first.GraphicsQualityLevel == second.GraphicsQualityLevel &&
                   first.MasterVolume == second.MasterVolume &&
                   first.MouseSensitivity == second.MouseSensitivity &&
                   first.LowerBackgroundProcessPriority == second.LowerBackgroundProcessPriority &&
                   RobloxSettingsService.NormalizeFrameRateCap(first.FrameRateCap) ==
                   RobloxSettingsService.NormalizeFrameRateCap(second.FrameRateCap);
        }

        private static string FormatSettingsNotice(string? notice)
        {
            return string.IsNullOrWhiteSpace(notice) ? string.Empty : $" {notice}";
        }

        // Запуск всех выбранных аккаунтов по очереди
        private async void LaunchSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = _config.Accounts.Where(a => a.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(
                    "Отметьте галочками аккаунты, которые нужно запустить одновременно (или нажмите ▶ Играть напротив нужного аккаунта).",
                    "Аккаунты не выбраны",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (MultiRobloxToggle.IsChecked != true)
            {
                MultiRobloxToggle.IsChecked = true;
                _multiRobloxService.Enable();
            }

            if (!int.TryParse(DelaySecondsBox.Text.Trim(), out int extraDelaySec) || extraDelaySec < 1 || extraDelaySec > 60)
            {
                extraDelaySec = 6;
                DelaySecondsBox.Text = "6";
            }

            LaunchSelectedBtn.IsEnabled = false;
            try
            {
                int confirmedCount = 0;
                string? stopReason = null;
                string? settingsNotice = null;
                var rateLimitedAccounts = new List<string>();

                for (int i = 0; i < selected.Count; i++)
                {
                    var acc = selected[i];
                    string? nextUser = (i < selected.Count - 1) ? selected[i + 1].Username : null;
                    FooterStatusText.Text = $"Массовый запуск ({i + 1}/{selected.Count}): @{acc.Username}...";

                    bool confirmed = await LaunchAccountInternalAsync(acc, nextUser);
                    settingsNotice ??= _lastRobloxSettingsNotice;

                    if (!confirmed)
                    {
                        if (acc.StatusText.Contains("429", StringComparison.OrdinalIgnoreCase))
                        {
                            rateLimitedAccounts.Add(acc.Username);
                            continue;
                        }

                        stopReason = $"@{acc.Username}: {acc.StatusText}";
                        for (int remaining = i + 1; remaining < selected.Count; remaining++)
                        {
                            selected[remaining].StatusText = "Не запускался: предыдущий запуск не подтверждён";
                            selected[remaining].StatusColor = "#94A3B8";
                        }
                        break;
                    }

                    confirmedCount++;

                    if (i < selected.Count - 1)
                    {
                        FooterStatusText.Text = $"Клиент @{acc.Username} запущен. Запуск @{selected[i + 1].Username}...";
                        await Task.Delay(TimeSpan.FromSeconds(extraDelaySec));
                    }
                }

                string rateLimitNotice = rateLimitedAccounts.Count == 0
                    ? string.Empty
                    : $" Ошибка 429 у: {string.Join(", ", rateLimitedAccounts.Select(name => $"@{name}"))}.";
                FooterStatusText.Text = stopReason == null
                    ? $"Подтверждён запуск {confirmedCount} из {selected.Count} выбранных аккаунтов.{rateLimitNotice}{FormatSettingsNotice(settingsNotice)}"
                    : $"Подтверждён запуск {confirmedCount} из {selected.Count}. Очередь остановлена: {stopReason}.{rateLimitNotice}{FormatSettingsNotice(settingsNotice)}";
            }
            finally
            {
                LaunchSelectedBtn.IsEnabled = true;
            }
        }

        // Открытие сайта Roblox внутри программы под выбранным аккаунтом
        private void OpenAccountBrowser_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is RobloxAccount account)
            {
                string cookie = AccountStorageService.DecryptCookie(account.EncryptedCookie);
                if (string.IsNullOrEmpty(cookie))
                {
                    MessageBox.Show("Не удалось расшифровать сессию аккаунта. Пожалуйста, перезайдите.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var browserWin = new BrowserWindow(cookie, $"{account.DisplayName} (@{account.Username})")
                {
                    Owner = this
                };
                browserWin.ShowDialog();

                if (!string.IsNullOrEmpty(browserWin.CapturedCookie) && browserWin.CapturedUserInfo != null)
                {
                    AddOrUpdateAccountFromLogin(browserWin.CapturedCookie, browserWin.CapturedUserInfo, account.Group, account.Note);
                }
            }
        }

        // Редактирование группы / заметки / куки аккаунта
        private async void EditAccount_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is RobloxAccount account)
            {
                var dlg = new AccountEditDialog(
                    requireCookie: false,
                    title: $"Настройки @{account.Username}",
                    currentGroup: account.Group,
                    currentNote: account.Note)
                {
                    Owner = this
                };

                if (dlg.ShowDialog() == true)
                {
                    account.Group = dlg.SelectedGroup;
                    account.Note = dlg.EnteredNote;

                    if (!string.IsNullOrWhiteSpace(dlg.EnteredCookie))
                    {
                        var info = await _apiService.ValidateAndGetUserAsync(dlg.EnteredCookie);
                        if (info != null)
                        {
                            account.UserId = info.Id;
                            account.Username = info.Name;
                            account.DisplayName = info.DisplayName;
                            account.AvatarUrl = info.AvatarUrl;
                            account.EncryptedCookie = AccountStorageService.EncryptCookie(RobloxApiService.CleanCookie(dlg.EnteredCookie));
                            account.StatusText = "Кука обновлена";
                            account.StatusColor = "#4CAF50";
                        }
                    }

                    SaveCurrentConfig();
                    RefreshGroupFilterCombo();
                    ApplyFilters();
                }
            }
        }

        // Удаление аккаунта
        private void DeleteAccount_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is RobloxAccount account)
            {
                var res = MessageBox.Show(
                    $"Удалить аккаунт {account.DisplayName} (@{account.Username}) из менеджера?",
                    "Подтверждение удаления",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (res == MessageBoxResult.Yes)
                {
                    _config.Accounts.Remove(account);
                    SaveCurrentConfig();
                    RefreshGroupFilterCombo();
                    ApplyFilters();
                    FooterStatusText.Text = $"Аккаунт @{account.Username} удален.";
                }
            }
        }

        // Проверка валидности всех сохраненных сессий
        private async void RefreshAccounts_Click(object sender, RoutedEventArgs e)
        {
            if (_config.Accounts.Count == 0) return;

            FooterStatusText.Text = "Проверка всех аккаунтов...";
            int valid = 0;

            foreach (var acc in _config.Accounts)
            {
                acc.StatusText = "Проверка...";
                acc.StatusColor = "#94A3B8";
                string cookie = AccountStorageService.DecryptCookie(acc.EncryptedCookie);
                var info = await _apiService.ValidateAndGetUserAsync(cookie);
                if (info != null)
                {
                    acc.UserId = info.Id;
                    acc.Username = info.Name;
                    acc.DisplayName = info.DisplayName;
                    acc.AvatarUrl = info.AvatarUrl;
                    acc.StatusText = "Сессия активна";
                    acc.StatusColor = "#4CAF50";
                    valid++;
                }
                else
                {
                    acc.StatusText = "Сессия устарела (нужен вход)";
                    acc.StatusColor = "#EF4444";
                }
            }

            SaveCurrentConfig();
            FooterStatusText.Text = $"Проверка завершена: активны {valid} из {_config.Accounts.Count}.";
        }

        private void CloseAllRoblox_Click(object sender, RoutedEventArgs e)
        {
            int count = _multiRobloxService.GetRunningRobloxCount();
            if (count == 0)
            {
                FooterStatusText.Text = "Нет запущенных окон Roblox.";
                return;
            }

            var res = MessageBox.Show(
                $"Закрыть все запущенные окна Roblox ({count} шт.)?",
                "Закрытие окон Roblox",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res == MessageBoxResult.Yes)
            {
                _multiRobloxService.CloseAllRobloxInstances();
                FooterStatusText.Text = "Все окна Roblox закрыты.";
            }
        }

        private void TileRobloxWindows_Click(object sender, RoutedEventArgs e)
        {
            IntPtr myHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var (count, cols, rows, width, height) = WindowLayoutService.TileRobloxWindows(myHwnd);
            if (count == 0)
            {
                FooterStatusText.Text = "Нет открытых игровых окон Roblox для расстановки.";
                return;
            }

            FooterStatusText.Text = $"Расставлено окон Roblox: {count} шт. (сетка {cols}×{rows}, размер каждого: {width}×{height} пикс.).";
        }

        private void SavedPlacesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing || _isUpdatingPlaceInputsFromCombo) return;
            if (SavedPlacesCombo.SelectedItem is SavedPlace place)
            {
                _isUpdatingPlaceInputsFromCombo = true;
                try
                {
                    if (ReferenceEquals(place, EmptySavedPlaceItem))
                    {
                        PlaceInputTextBox.Text = string.Empty;
                        VipOrJobTextBox.Text = string.Empty;
                    }
                    else
                    {
                        PlaceInputTextBox.Text = place.PlaceId;
                        VipOrJobTextBox.Text = place.VipLinkOrJobId;
                    }
                    SaveCurrentConfig();
                }
                finally
                {
                    _isUpdatingPlaceInputsFromCombo = false;
                }
            }
        }

        private void SaveCurrentPlace_Click(object sender, RoutedEventArgs e)
        {
            string placeInput = PlaceInputTextBox.Text.Trim();
            string vipOrJobInput = VipOrJobTextBox.Text.Trim();
            var (placeId, _, jobId) = RobloxApiService.ParseGameTargets(placeInput, vipOrJobInput);
            string? shareCode = RobloxApiService.ExtractShareCode(PlaceInputTextBox.Text, VipOrJobTextBox.Text);

            if (placeId == null && string.IsNullOrEmpty(shareCode))
            {
                MessageBox.Show("Сначала введите корректный Place ID, ссылку на игру или ссылку на приватку (share?code=...).", "Внимание", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(placeInput))
            {
                placeInput = vipOrJobInput;
                vipOrJobInput = string.Empty;
            }

            var duplicate = _config.SavedPlaces.FirstOrDefault(place =>
                string.Equals(place.PlaceId.Trim(), placeInput, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(place.VipLinkOrJobId.Trim(), vipOrJobInput, StringComparison.OrdinalIgnoreCase));
            if (duplicate != null)
            {
                FooterStatusText.Text = $"Это место уже сохранено как «{duplicate.Name}».";
                SavedPlacesCombo.SelectedItem = duplicate;
                return;
            }

            string suggestedName = !string.IsNullOrEmpty(shareCode)
                ? "Приватный сервер"
                : placeId != null
                    ? $"Игра {placeId.Value}"
                    : !string.IsNullOrEmpty(jobId) ? $"Сервер {jobId}" : "Место Roblox";
            var nameDialog = new SavedPlaceNameDialog(suggestedName) { Owner = this };
            if (nameDialog.ShowDialog() != true)
                return;

            var newPlace = new SavedPlace
            {
                Name = nameDialog.PlaceName,
                PlaceId = placeInput,
                VipLinkOrJobId = vipOrJobInput
            };

            _config.SavedPlaces.Add(newPlace);
            RefreshSavedPlacesCombo();
            SavedPlacesCombo.SelectedItem = newPlace;
            SaveCurrentConfig();
            FooterStatusText.Text = "Добавлено в избранное!";
        }

        private void DeleteSavedPlace_Click(object sender, RoutedEventArgs e)
        {
            if (SavedPlacesCombo.SelectedItem is not SavedPlace place || ReferenceEquals(place, EmptySavedPlaceItem))
            {
                FooterStatusText.Text = "Сначала выберите избранное для удаления.";
                return;
            }

            var result = MessageBox.Show(
                $"Удалить избранное «{place.Name}»?",
                "Удаление избранного",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                return;

            _config.SavedPlaces.Remove(place);
            RefreshSavedPlacesCombo();
            SaveCurrentConfig();
            FooterStatusText.Text = $"Избранное «{place.Name}» удалено.";
        }

        private void RobloxSettings_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new RobloxSettingsDialog(_config.RobloxSettings ?? new RobloxGameSettings())
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true)
                return;

            RobloxGameSettings? previousSettings = _config.RobloxSettings;
            _config.RobloxSettings = dialog.Settings;
            if (!SaveCurrentConfig())
            {
                _config.RobloxSettings = previousSettings;
                MessageBox.Show(
                    "Настройки не сохранены. Проверьте доступ к файлу конфигурации в папке AppData.",
                    "Ошибка сохранения настроек",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            _lastRobloxSettingsNotice = TryApplyRobloxSettingsBeforeLaunch();
            FooterStatusText.Text = _lastRobloxSettingsNotice == null
                ? $"Настройки записаны в конфигурацию Roblox (лимит FPS: {FormatFrameRateCap(dialog.Settings.FrameRateCap)})."
                : $"Настройки сохранены и применятся после закрытия игровых окон Roblox. {_lastRobloxSettingsNotice}";
        }

        private static string FormatFrameRateCap(int frameRateCap)
        {
            int normalized = RobloxSettingsService.NormalizeFrameRateCap(frameRateCap);
            return normalized == -1 ? "по умолчанию" : $"{normalized}";
        }

        private void DelaySecondsBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SaveCurrentConfig();
        }

        private void PlaceInputTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isInitializing || _isUpdatingPlaceInputsFromCombo) return;

            if (SavedPlacesCombo.SelectedItem is SavedPlace selected && !ReferenceEquals(selected, EmptySavedPlaceItem))
            {
                bool matchesSelected =
                    string.Equals(PlaceInputTextBox.Text.Trim(), selected.PlaceId.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(VipOrJobTextBox.Text.Trim(), selected.VipLinkOrJobId.Trim(), StringComparison.OrdinalIgnoreCase);

                if (!matchesSelected)
                {
                    _isUpdatingPlaceInputsFromCombo = true;
                    try
                    {
                        SavedPlacesCombo.SelectedItem = EmptySavedPlaceItem;
                    }
                    finally
                    {
                        _isUpdatingPlaceInputsFromCombo = false;
                    }
                }
            }

            SaveCurrentConfig();
        }

        private void SelectAllCheckBox_Click(object sender, RoutedEventArgs e)
        {
            bool select = SelectAllCheckBox.IsChecked == true;
            foreach (var acc in _filteredAccounts)
            {
                acc.IsSelected = select;
            }
            UpdateSelectionSummary();
        }

        private void AccountSelection_Click(object sender, RoutedEventArgs e)
        {
            UpdateSelectionSummary();
        }

        private void GroupFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            ApplyFilters();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isInitializing) return;
            ApplyFilters();
        }

        private async Task CheckForUpdatesSilentlyAsync()
        {
            try
            {
                var (release, _) = await _updateService.CheckForUpdateAsync();
                if (release != null && release.IsUpdateAvailable)
                {
                    _cachedAvailableUpdate = release;
                    UpdateAppBtn.Content = $"⬇ Обновить ({release.TagName})";
                    UpdateAppBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2563EB"));
                    UpdateAppBtn.Foreground = Brushes.White;
                    FooterStatusText.Text = $"Доступна новая версия {release.TagName}! Нажмите «⬇ Обновить» внизу справа.";
                }
            }
            catch { }
        }

        private async void UpdateApp_Click(object sender, RoutedEventArgs e)
        {
            UpdateAppBtn.IsEnabled = false;
            try
            {
                FooterStatusText.Text = "Проверка обновлений на GitHub...";
                var (release, error) = await _updateService.CheckForUpdateAsync();
                if (release == null)
                {
                    MessageBox.Show(
                        $"Не удалось проверить обновления: {error}",
                        "Обновление",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    FooterStatusText.Text = $"Ошибка проверки обновлений: {error}";
                    return;
                }

                Version currentVer = GitHubUpdateService.GetCurrentVersion();
                if (!release.IsUpdateAvailable)
                {
                    var reinstallAsk = MessageBox.Show(
                        $"У вас уже установлена актуальная версия (v{currentVer} / GitHub: {release.TagName}).\n\nПерекачать и обновить файлы принудительно?",
                        "Обновление не требуется",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);
                    if (reinstallAsk != MessageBoxResult.Yes)
                    {
                        FooterStatusText.Text = $"Установлена последняя версия ({release.TagName}).";
                        return;
                    }
                }
                else
                {
                    var confirm = MessageBox.Show(
                        $"Доступна новая версия {release.TagName} (текущая: v{currentVer}).\n\nСкачать обновление с GitHub и перезапустить менеджер сейчас?\n(Уже открытые окна Roblox закрыты НЕ будут!)",
                        "Доступно обновление",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    if (confirm != MessageBoxResult.Yes)
                        return;
                }

                var (ok, msg) = await _updateService.DownloadAndApplyUpdateAsync(
                    release,
                    progress => Dispatcher.Invoke(() => FooterStatusText.Text = progress));

                if (ok && msg == "RESTART_REQUIRED")
                {
                    FooterStatusText.Text = "Обновление скачано! Перезапуск...";
                    Application.Current.Shutdown();
                    return;
                }

                FooterStatusText.Text = msg;
                if (!ok)
                {
                    MessageBox.Show(msg, "Ошибка обновления", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                DiagnosticsService.LogError("UI.UpdateApp", "Сбой при обновлении", ex);
                FooterStatusText.Text = $"Ошибка обновления: {ex.Message}";
            }
            finally
            {
                UpdateAppBtn.IsEnabled = true;
            }
        }

        private void ExportErrorLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int runningCount = _multiRobloxService.GetRunningRobloxCount();
                string report = DiagnosticsService.ExportCompactReport(_config.Accounts.Count, runningCount);

                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Сохранить компактный лог ошибок",
                    FileName = $"RobloxManager_Log_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                    DefaultExt = ".txt",
                    Filter = "Текстовый лог (*.txt)|*.txt|Все файлы (*.*)|*.*"
                };

                if (dlg.ShowDialog(this) == true)
                {
                    File.WriteAllText(dlg.FileName, report, System.Text.Encoding.UTF8);
                    FooterStatusText.Text = $"Компактный лог сохранён: {Path.GetFileName(dlg.FileName)}";
                }
            }
            catch (Exception ex)
            {
                DiagnosticsService.LogError("UI.ExportLog", "Не удалось экспортировать лог", ex);
                MessageBox.Show($"Ошибка сохранения лога: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CreateDesktopShortcut_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                UpdateDesktopShortcut();

                FooterStatusText.Text = "Ярлык успешно создан на Рабочем столе!";
                MessageBox.Show("Ярлык «Roblox Account Manager» успешно создан на вашем Рабочем столе!", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка создания ярлыка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void UpdateDesktopShortcut()
        {
            string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                throw new InvalidOperationException("Не удалось определить текущий exe-файл.");

            string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string shortcutPath = Path.Combine(desktopDir, "Roblox Account Manager.lnk");
            Type shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("Компонент создания ярлыков Windows недоступен.");

            object? shell = null;
            object? shortcut = null;
            try
            {
                shell = Activator.CreateInstance(shellType)
                    ?? throw new InvalidOperationException("Не удалось создать компонент Windows Script Host.");
                dynamic shellAutomation = shell;
                shortcut = shellAutomation.CreateShortcut(shortcutPath);
                dynamic shortcutAutomation = shortcut;
                shortcutAutomation.TargetPath = exePath;
                shortcutAutomation.WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty;
                shortcutAutomation.Description = "Roblox Account Manager";
                shortcutAutomation.Save();
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut))
                    Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell))
                    Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}
