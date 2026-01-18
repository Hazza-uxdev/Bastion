using SecureVault.Models;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace SecureVault
{
    public partial class EntryWindow : Window
    {
        public VaultEntry Entry { get; private set; }
        private static readonly Random _rng = new Random();

        public EntryWindow(VaultEntry entry = null)
        {
            InitializeComponent();

            if (entry != null)
            {
                Entry = entry;
                TitleBox.Text = entry.Title;
                UsernameBox.Text = entry.Username;
                PasswordBox.Password = entry.Password;
            }
            else
            {
                Entry = new VaultEntry();
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Entry.Title = TitleBox.Text;
            Entry.Username = UsernameBox.Text;
            Entry.Password = PasswordBox.Password;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        // ---------------- Password Generator ----------------

        private string GenerateRandomPassword(int length = 16)
        {
            const string upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            const string lower = "abcdefghijklmnopqrstuvwxyz";
            const string digits = "0123456789";
            const string symbols = "!@#$%^&*()-_=+[]{}|;:,.<>?";

            string allChars = upper + lower + digits + symbols;

            char[] password = new char[length];

            // Ensure at least 1 char from each set
            password[0] = upper[_rng.Next(upper.Length)];
            password[1] = lower[_rng.Next(lower.Length)];
            password[2] = digits[_rng.Next(digits.Length)];
            password[3] = symbols[_rng.Next(symbols.Length)];

            for (int i = 4; i < length; i++)
                password[i] = allChars[_rng.Next(allChars.Length)];

            // Shuffle the password to randomize first 4 chars
            return new string(password.OrderBy(x => _rng.Next()).ToArray());
        }

        private void GeneratePassword_Click(object sender, RoutedEventArgs e)
        {
            PasswordBox.Password = GenerateRandomPassword();
        }

        private void CopyPassword_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(PasswordBox.Password))
            {
                Clipboard.SetText(PasswordBox.Password);
                MessageBox.Show("Password copied to clipboard!", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}
