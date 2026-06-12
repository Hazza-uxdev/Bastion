using System.Windows;
using System.Windows.Input;

namespace SecureVault
{
    public partial class FolderNameDialog : Window
    {
        public string FolderName => FolderBox.Text.Trim();

        public FolderNameDialog()
        {
            InitializeComponent();
            Loaded += (_, _) => FolderBox.Focus();
        }

        private void Create_Click(object sender, RoutedEventArgs e) => DialogResult = true;
        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
        private void FolderBox_KeyDown(object sender, KeyEventArgs e)
        { if (e.Key == Key.Enter) DialogResult = true; if (e.Key == Key.Escape) DialogResult = false; }
    }
}
