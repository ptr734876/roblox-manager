using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;

namespace RobloxAccountManager.Services
{
    public class RobloxSettingsService
    {
        private readonly string _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Roblox",
            "GlobalBasicSettings_13.xml");

        public void Apply(RobloxGameSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            if (!File.Exists(_settingsPath))
            {
                throw new FileNotFoundException(
                    "Файл настроек Roblox не найден. Запустите Roblox один раз и повторите запуск аккаунта.",
                    _settingsPath);
            }

            var document = new XmlDocument { PreserveWhitespace = true };
            var readerSettings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };

            using (var reader = XmlReader.Create(_settingsPath, readerSettings))
            {
                document.Load(reader);
            }

            XmlElement root = document.DocumentElement
                ?? throw new InvalidDataException("Файл настроек Roblox не содержит корневой элемент.");

            XmlElement userGameSettings = root.ChildNodes
                .OfType<XmlElement>()
                .FirstOrDefault(element =>
                    element.Name == "Item" &&
                    element.GetAttribute("class") == "UserGameSettings")
                ?? throw new InvalidDataException("В файле настроек Roblox не найден раздел UserGameSettings.");

            XmlElement properties = userGameSettings.ChildNodes
                .OfType<XmlElement>()
                .FirstOrDefault(element => element.Name == "Properties")
                ?? throw new InvalidDataException("В разделе UserGameSettings не найден список Properties.");

            SetValue(document, properties, "token", "SavedQualityLevel",
                settings.AutomaticGraphics
                    ? "0"
                    : Math.Clamp(settings.GraphicsQualityLevel, 1, 10).ToString(CultureInfo.InvariantCulture));
            SetValue(document, properties, "float", "MasterVolume",
                Math.Clamp(settings.MasterVolume, 0f, 1f).ToString("0.##", CultureInfo.InvariantCulture));
            SetValue(document, properties, "float", "MouseSensitivity",
                Math.Clamp(settings.MouseSensitivity, 0f, 4f).ToString("0.##", CultureInfo.InvariantCulture));
            SetValue(document, properties, "int", "FramerateCap",
                NormalizeFrameRateCap(settings.FrameRateCap).ToString(CultureInfo.InvariantCulture));

            string temporaryPath = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var writerSettings = new XmlWriterSettings
                {
                    Encoding = new System.Text.UTF8Encoding(false),
                    Indent = false,
                    NewLineHandling = NewLineHandling.None
                };
                using (var writer = XmlWriter.Create(temporaryPath, writerSettings))
                {
                    document.Save(writer);
                }

                File.Move(temporaryPath, _settingsPath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        public static int NormalizeFrameRateCap(int frameRateCap)
        {
            return frameRateCap is -1 or 15 or 30 or 60 or 120 or 144 or 240
                ? frameRateCap
                : -1;
        }

        private static void SetValue(XmlDocument document, XmlElement properties, string elementName, string settingName, string value)
        {
            XmlElement? setting = properties.ChildNodes
                .OfType<XmlElement>()
                .FirstOrDefault(element =>
                    element.Name == elementName &&
                    element.GetAttribute("name") == settingName);

            if (setting == null)
            {
                setting = document.CreateElement(elementName);
                setting.SetAttribute("name", settingName);
                properties.AppendChild(setting);
            }

            setting.InnerText = value;
        }
    }
}
