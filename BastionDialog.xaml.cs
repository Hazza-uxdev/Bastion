using System.Windows;
using System.Windows.Input;

namespace SecureVault
{
    public partial class BastionDialog : Window
    {
        public BastionDialog(string title, string body, bool isConfirm = true)
        {
            InitializeComponent();
            TitleText.Text = title;
            BodyText.Text = body;

            if (!isConfirm)
            {
                CancelBtn.Visibility = Visibility.Collapsed;
                ConfirmBtn.Content = "OK";
                IconText.Text = "✓";
            }
        }

        private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
