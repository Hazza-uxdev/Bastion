using System.Windows;
using System.Windows.Input;

namespace SecureVault
{
    public partial class PasswordPromptDialog : Window
    {
        public string Password => PasswordBox.Password;

        public PasswordPromptDialog(string title, string body)
        {
            InitializeComponent();
            TitleText.Text = title;
            BodyText.Text = body;
            Loaded += (_, _) => PasswordBox.Focus();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                DialogResult = true;
        }

        private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
