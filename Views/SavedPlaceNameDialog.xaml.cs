using System.Windows;

namespace RobloxAccountManager.Views
{
    public partial class SavedPlaceNameDialog : Window
    {
        public string PlaceName => PlaceNameTextBox.Text.Trim();

        public SavedPlaceNameDialog(string suggestedName)
        {
            InitializeComponent();
            PlaceNameTextBox.Text = suggestedName;
            Loaded += (_, _) => FitToWorkArea();
            Loaded += (_, _) =>
            {
                PlaceNameTextBox.Focus();
                PlaceNameTextBox.SelectAll();
            };
        }

        private void FitToWorkArea()
        {
            double availableHeight = Math.Max(190, SystemParameters.WorkArea.Height - 32);
            double availableWidth = Math.Max(360, SystemParameters.WorkArea.Width - 32);

            MaxHeight = availableHeight;
            MaxWidth = availableWidth;
            Width = Math.Min(440, availableWidth);
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(PlaceNameTextBox.Text))
            {
                MessageBox.Show("Введите название избранного.", "Пустое название", MessageBoxButton.OK, MessageBoxImage.Warning);
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
