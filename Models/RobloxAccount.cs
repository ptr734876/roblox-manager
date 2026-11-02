using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace RobloxAccountManager.Models
{
    public class RobloxAccount : INotifyPropertyChanged
    {
        private bool _isSelected;
        private string _username = string.Empty;
        private string _displayName = string.Empty;
        private long _userId;
        private string _avatarUrl = string.Empty;
        private string _group = "Основные";
        private string _note = string.Empty;
        private string _statusText = "Готов к запуску";
        private string _statusColor = "#4CAF50";
        private DateTime _lastUsed = DateTime.Now;

        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        [JsonIgnore]
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public long UserId
        {
            get => _userId;
            set { _userId = value; OnPropertyChanged(); }
        }

        public string Username
        {
            get => _username;
            set { _username = value; OnPropertyChanged(); OnPropertyChanged(nameof(FormattedUsername)); }
        }

        public string DisplayName
        {
            get => _displayName;
            set { _displayName = value; OnPropertyChanged(); }
        }

        [JsonIgnore]
        public string FormattedUsername => string.IsNullOrEmpty(Username) ? "" : $"@{Username}";

        public string AvatarUrl
        {
            get => _avatarUrl;
            set { _avatarUrl = value; OnPropertyChanged(); }
        }

        public string Group
        {
            get => string.IsNullOrWhiteSpace(_group) ? "Без группы" : _group;
            set { _group = value; OnPropertyChanged(); }
        }

        public string Note
        {
            get => _note;
            set { _note = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Зашифрованный через Windows DPAPI токен .ROBLOSECURITY (Base64)
        /// </summary>
        public string EncryptedCookie { get; set; } = string.Empty;

        public DateTime LastUsed
        {
            get => _lastUsed;
            set { _lastUsed = value; OnPropertyChanged(); OnPropertyChanged(nameof(LastUsedFormatted)); }
        }

        [JsonIgnore]
        public string LastUsedFormatted => LastUsed.ToString("dd.MM.yyyy HH:mm");

        [JsonIgnore]
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        [JsonIgnore]
        public string StatusColor
        {
            get => _statusColor;
            set { _statusColor = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public class SavedPlace
    {
        public string Name { get; set; } = string.Empty;
        public string PlaceId { get; set; } = string.Empty;
        public string VipLinkOrJobId { get; set; } = string.Empty;

        public override string ToString() => string.IsNullOrWhiteSpace(Name) ? PlaceId : Name;
    }
}
