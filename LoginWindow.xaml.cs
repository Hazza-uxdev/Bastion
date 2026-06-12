using System.IO;
using System.Windows;
using System.Windows.Input;
using SecureVault.Models;
using SecureVault.Storage;

namespace SecureVault;

public partial class LoginWindow : Window
{
    private const string VaultFileName = "vault.dat";

    public LoginWindow()
    {
        InitializeComponent();
        UpdateWatermark();
        StatusText.Visibility = Visibility.Collapsed;
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Login_Click(object sender, RoutedEventArgs e) => TryUnlock();

    private void Forgot_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "If you forget the master password, the vault cannot be decrypted.\n\n" +
            "If you have a backup you still know the password for, restore from that backup.",
            "Forgot password",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        UpdateWatermark();
        StatusText.Visibility = Visibility.Collapsed;
    }

    private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) TryUnlock();
    }

    private void TryUnlock()
    {
        var password = PasswordBox.Password;

        if (string.IsNullOrWhiteSpace(password))
        {
            StatusText.Text = "Please enter your master password.";
            StatusText.Visibility = Visibility.Visible;
            PasswordBox.Focus();
            return;
        }

        try
        {
            if (!File.Exists(VaultFileName))
                VaultStore.Save(new Vault(), password);

            var vault = VaultStore.Load(password);
            var main = new MainWindow(vault, password);
            main.Show();
            Close();
        }
        catch
        {
            StatusText.Text = "Incorrect password. Please try again.";
            StatusText.Visibility = Visibility.Visible;
            PasswordBox.Focus();
            PasswordBox.SelectAll();
        }
    }

    private void UpdateWatermark()
    {
        PasswordWatermark.Visibility =
            string.IsNullOrEmpty(PasswordBox.Password) ? Visibility.Visible : Visibility.Collapsed;
    }
}
