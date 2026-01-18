using System.Windows;
using SecureVault.Storage;
using SecureVault.Models;

namespace SecureVault;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
    }

    private void Unlock_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // First run: create vault if missing
            if (!System.IO.File.Exists("vault.dat"))
            {
                VaultStore.Save(new Vault(), PasswordBox.Password);
            }

            var vault = VaultStore.Load(PasswordBox.Password);

            MainWindow main = new MainWindow(vault, PasswordBox.Password);
            main.Show();

            Close();
        }
        catch
        {
            MessageBox.Show("Invalid password or vault missing.");
        }
    }
}
