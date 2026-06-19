using System;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using SecureVault.Models;
using SecureVault.Storage;

namespace SecureVault;

public partial class LoginWindow : Window
{
    private System.Windows.Forms.NotifyIcon? _lockedTrayIcon;
    private bool _allowCloseFromTray;
    private bool _shownFromTray;

    public LoginWindow()
    {
        InitializeComponent();
        UpdateWatermark();
        StatusText.Visibility = Visibility.Collapsed;
        Loaded += LoginWindow_Loaded;
    }

    private void LoginWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!App.StartHiddenRequested) return;
        EnsureLockedTrayIcon();
        Hide();
    }

    private void EnsureLockedTrayIcon()
    {
        if (_lockedTrayIcon != null) return;

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Unlock Bastion", null, (_, _) => ShowLoginFromTray());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _allowCloseFromTray = true;
            Close();
        });

        _lockedTrayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath)
                   ?? System.Drawing.SystemIcons.Application,
            Text = "Bastion is locked",
            Visible = true,
            ContextMenuStrip = menu
        };
        _lockedTrayIcon.DoubleClick += (_, _) => ShowLoginFromTray();
    }

    private void ShowLoginFromTray()
    {
        _shownFromTray = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        PasswordBox.Focus();
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
            if (!VaultStore.Exists())
                VaultStore.Save(new Vault(), password);

            var vault = VaultStore.Load(password);
            var main = new MainWindow(vault, password, App.StartHiddenRequested && !_shownFromTray);
            main.Show();
            _allowCloseFromTray = true;
            Close();
        }
        catch (CryptographicException)
        {
            StatusText.Text = "Incorrect password. Please try again.";
            StatusText.Visibility = Visibility.Visible;
            PasswordBox.Focus();
            PasswordBox.SelectAll();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Vault storage error: {ex.Message}";
            StatusText.Visibility = Visibility.Visible;
            PasswordBox.Focus();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not open vault: {ex.Message}";
            StatusText.Visibility = Visibility.Visible;
            PasswordBox.Focus();
        }
    }

    private void UpdateWatermark()
    {
        PasswordWatermark.Visibility =
            string.IsNullOrEmpty(PasswordBox.Password) ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnClosed(EventArgs e)
    {
        _lockedTrayIcon?.Dispose();
        _lockedTrayIcon = null;
        base.OnClosed(e);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (App.StartHiddenRequested && !_allowCloseFromTray && !IsVisible)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }
}
