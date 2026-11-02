using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.Views
{
    public partial class RobloxSettingsDialog : Window
    {
        public RobloxGameSettings Settings => new()
        {
            AutomaticGraphics = AutomaticGraphicsCheckBox.IsChecked == true,
            GraphicsQualityLevel = (int)GraphicsQualitySlider.Value,
            MasterVolume = (float)(VolumeSlider.Value / 100d),
            MouseSensitivity = (float)SensitivitySlider.Value,
            LowerBackgroundProcessPriority = LowerBackgroundPriorityCheckBox.IsChecked == true,
            FrameRateCap = int.TryParse(
                (FrameRateCapComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int cap) ? cap : -1,
            LaunchTimeoutSeconds = ParseClampedInt(LaunchTimeoutBox.Text, 210, 30, 900),
            StartupGracePeriodSeconds = ParseClampedInt(StartupGraceBox.Text, 180, 15, 900),
            TeleportProtectionSeconds = ParseClampedInt(TeleportProtectionBox.Text, 150, 15, 900),
            DisconnectConfirmSeconds = ParseClampedInt(DisconnectConfirmBox.Text, 20, 3, 300),
            AntiAfkIntervalMinutes = ParseClampedInt(AntiAfkIntervalBox.Text, 12, 1, 18)
        };

        private static int ParseClampedInt(string? text, int defaultValue, int min, int max)
        {
            if (int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int val))
                return Math.Clamp(val, min, max);
            return defaultValue;
        }

        public RobloxSettingsDialog(RobloxGameSettings settings)
        {
            InitializeComponent();
            Loaded += (_, _) => FitToWorkArea();

            AutomaticGraphicsCheckBox.IsChecked = settings.AutomaticGraphics;
            GraphicsQualitySlider.Value = Math.Clamp(settings.GraphicsQualityLevel, 1, 10);
            VolumeSlider.Value = Math.Clamp(settings.MasterVolume, 0f, 1f) * 100d;
            SensitivitySlider.Value = Math.Clamp(settings.MouseSensitivity, 0f, 4f);
            LowerBackgroundPriorityCheckBox.IsChecked = settings.LowerBackgroundProcessPriority;
            FrameRateCapComboBox.SelectedValue =
                RobloxSettingsService.NormalizeFrameRateCap(settings.FrameRateCap)
                    .ToString(CultureInfo.InvariantCulture);

            int launchTimeout = settings.LaunchTimeoutSeconds > 0 ? settings.LaunchTimeoutSeconds : 210;
            int startupGrace = settings.StartupGracePeriodSeconds > 0 ? settings.StartupGracePeriodSeconds : 180;
            int teleportProt = settings.TeleportProtectionSeconds > 0 ? settings.TeleportProtectionSeconds : 150;
            int disconnectConfirm = settings.DisconnectConfirmSeconds > 0 ? settings.DisconnectConfirmSeconds : 20;
            int antiAfkMin = settings.AntiAfkIntervalMinutes > 0 ? settings.AntiAfkIntervalMinutes : 12;

            LaunchTimeoutBox.Text = Math.Clamp(launchTimeout, 30, 900).ToString(CultureInfo.InvariantCulture);
            StartupGraceBox.Text = Math.Clamp(startupGrace, 15, 900).ToString(CultureInfo.InvariantCulture);
            TeleportProtectionBox.Text = Math.Clamp(teleportProt, 15, 900).ToString(CultureInfo.InvariantCulture);
            DisconnectConfirmBox.Text = Math.Clamp(disconnectConfirm, 3, 300).ToString(CultureInfo.InvariantCulture);
            AntiAfkIntervalBox.Text = Math.Clamp(antiAfkMin, 1, 18).ToString(CultureInfo.InvariantCulture);

            UpdateGraphicsControls();
            UpdateValueLabels();
        }

        private void ResetTimeoutsBtn_Click(object sender, RoutedEventArgs e)
        {
            LaunchTimeoutBox.Text = "210";
            StartupGraceBox.Text = "180";
            TeleportProtectionBox.Text = "150";
            DisconnectConfirmBox.Text = "20";
            AntiAfkIntervalBox.Text = "12";
        }

        private void FitToWorkArea()
        {
            double availableHeight = Math.Max(300, SystemParameters.WorkArea.Height - 32);
            double availableWidth = Math.Max(420, SystemParameters.WorkArea.Width - 32);

            MaxHeight = availableHeight;
            MaxWidth = availableWidth;
            MinHeight = Math.Min(300, availableHeight);
            MinWidth = Math.Min(420, availableWidth);
            Height = Math.Min(620, availableHeight);
            Width = Math.Min(520, availableWidth);
        }

        private void GraphicsMode_Changed(object sender, RoutedEventArgs e)
        {
            UpdateGraphicsControls();
        }

        private void GraphicsQualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (GraphicsLevelLabel != null)
                GraphicsLevelLabel.Text = $"Уровень качества: {(int)GraphicsQualitySlider.Value}";
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (VolumeValueLabel != null)
                VolumeValueLabel.Text = $"{(int)VolumeSlider.Value}%";
        }

        private void SensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SensitivityValueLabel != null)
                SensitivityValueLabel.Text = SensitivitySlider.Value.ToString("0.0", CultureInfo.CurrentCulture);
        }

        private void UpdateGraphicsControls()
        {
            if (AutomaticGraphicsCheckBox == null || GraphicsQualitySlider == null)
                return;

            GraphicsQualitySlider.IsEnabled = AutomaticGraphicsCheckBox.IsChecked != true;
            GraphicsQualitySlider.Opacity = GraphicsQualitySlider.IsEnabled ? 1 : 0.5;
        }

        private void UpdateValueLabels()
        {
            GraphicsLevelLabel.Text = $"Уровень качества: {(int)GraphicsQualitySlider.Value}";
            VolumeValueLabel.Text = $"{(int)VolumeSlider.Value}%";
            SensitivityValueLabel.Text = SensitivitySlider.Value.ToString("0.0", CultureInfo.CurrentCulture);
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
