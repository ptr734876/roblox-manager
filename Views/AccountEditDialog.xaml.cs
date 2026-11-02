using System.Windows;

namespace RobloxAccountManager.Views
{
    public partial class AccountEditDialog : Window
    {
        private readonly bool _requireCookie;

        public string EnteredCookie => CookieTextBox.Text.Trim();
        public string SelectedGroup => string.IsNullOrWhiteSpace(GroupComboBox.Text) ? "Основные" : GroupComboBox.Text.Trim();
        public string EnteredNote => NoteTextBox.Text.Trim();

        public AccountEditDialog(bool requireCookie, string title, string currentGroup = "Основные", string currentNote = "")
        {
            InitializeComponent();
            _requireCookie = requireCookie;
            Title = title;
            DialogTitleText.Text = title;
            GroupComboBox.Text = string.IsNullOrWhiteSpace(currentGroup) ? "Основные" : currentGroup;
            NoteTextBox.Text = currentNote ?? string.Empty;

            if (requireCookie)
            {
                SaveBtn.Content = "Проверить и добавить";
            }
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_requireCookie && string.IsNullOrWhiteSpace(CookieTextBox.Text))
            {
                MessageBox.Show("Пожалуйста, вставьте куку .ROBLOSECURITY", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
            Close();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
