using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.Views
{
    public partial class BrowserWindow : Window
    {
        private readonly string? _existingCookie;
        private readonly bool _isLoginCaptureMode;
        private readonly RobloxApiService _apiService = new();
        private DispatcherTimer? _cookiePollTimer;
        private bool _isCapturing;

        public string? CapturedCookie { get; private set; }
        public RobloxUserInfo? CapturedUserInfo { get; private set; }

        public BrowserWindow(string? existingCookie = null, string? accountTitle = null)
        {
            InitializeComponent();
            _existingCookie = existingCookie;
            _isLoginCaptureMode = string.IsNullOrEmpty(existingCookie);

            if (!_isLoginCaptureMode)
            {
                Title = $"Браузер Roblox — {accountTitle}";
                HeaderStatusText.Text = $"Аккаунт: {accountTitle} (изолированная сессия браузера)";
                ManualCaptureBtn.Content = "Обновить сессию";
            }

            Loaded += BrowserWindow_Loaded;
            Closed += BrowserWindow_Closed;
        }

        private async void BrowserWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RobloxAccountManager",
                    "WebView2_Session");

                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await Browser.EnsureCoreWebView2Async(env);

                // Очищаем старые куки перед новым входом или перед инжектом нужного аккаунта
                Browser.CoreWebView2.CookieManager.DeleteAllCookies();

                if (!string.IsNullOrEmpty(_existingCookie))
                {
                    var cookie = Browser.CoreWebView2.CookieManager.CreateCookie(
                        ".ROBLOSECURITY",
                        RobloxApiService.CleanCookie(_existingCookie),
                        ".roblox.com",
                        "/");
                    cookie.IsHttpOnly = true;
                    cookie.IsSecure = true;
                    Browser.CoreWebView2.CookieManager.AddOrUpdateCookie(cookie);
                    Browser.CoreWebView2.Navigate("https://www.roblox.com/home");
                }
                else
                {
                    Browser.CoreWebView2.Navigate("https://www.roblox.com/login");
                }

                Browser.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;

                if (_isLoginCaptureMode)
                {
                    _cookiePollTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(1.8)
                    };
                    _cookiePollTimer.Tick += async (_, _) => await TryCaptureCookieAsync(silent: true);
                    _cookiePollTimer.Start();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Не удалось запустить WebView2: {ex.Message}\nУбедитесь, что в системе установлен Microsoft Edge WebView2 Runtime.",
                    "Ошибка WebView2",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Close();
            }
        }

        private async void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            if (_isLoginCaptureMode)
            {
                await TryCaptureCookieAsync(silent: true);
            }
        }

        private async Task TryCaptureCookieAsync(bool silent)
        {
            if (_isCapturing || Browser.CoreWebView2 == null)
                return;

            _isCapturing = true;
            try
            {
                var cookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync("https://www.roblox.com");
                var robloCookie = cookies.FirstOrDefault(c => c.Name.Equals(".ROBLOSECURITY", StringComparison.OrdinalIgnoreCase));

                if (robloCookie != null && !string.IsNullOrWhiteSpace(robloCookie.Value))
                {
                    HeaderStatusText.Text = "Обнаружена сессия! Проверяем аккаунт...";
                    var userInfo = await _apiService.ValidateAndGetUserAsync(robloCookie.Value);
                    if (userInfo != null)
                    {
                        CapturedCookie = robloCookie.Value;
                        CapturedUserInfo = userInfo;
                        _cookiePollTimer?.Stop();

                        if (_isLoginCaptureMode)
                        {
                            DialogResult = true;
                            Close();
                            return;
                        }
                        else if (!silent)
                        {
                            HeaderStatusText.Text = $"Сессия аккаунта @{userInfo.Name} успешно обновлена!";
                        }
                    }
                }
                else if (!silent)
                {
                    MessageBox.Show(
                        "Кука .ROBLOSECURITY еще не найдена. Пожалуйста, завершите вход в аккаунт (включая 2FA/капчу, если требуется).",
                        "Вход не завершен",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch
            {
                // Ignore transient errors
            }
            finally
            {
                _isCapturing = false;
            }
        }

        private async void ManualCaptureBtn_Click(object sender, RoutedEventArgs e)
        {
            await TryCaptureCookieAsync(silent: false);
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void BrowserWindow_Closed(object? sender, EventArgs e)
        {
            _cookiePollTimer?.Stop();
        }
    }
}
