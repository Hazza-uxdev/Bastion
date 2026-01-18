using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SecureVault.Models;
using SecureVault.Storage;
using System.Windows.Input;

namespace SecureVault;

public partial class MainWindow : Window
{
    private Vault _vault;
    private string _password;
    private DispatcherTimer _lockTimer;

    public MainWindow(Vault vault, string password)
    {
        InitializeComponent();
        _vault = vault;
        _password = password;

        VaultList.ItemsSource = _vault.Entries;

        // Lock timer
        _lockTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        _lockTimer.Tick += (_, _) => Lock();
        _lockTimer.Start();

        MouseMove += (_, _) =>
        {
            _lockTimer.Stop();
            _lockTimer.Start();
        };

        // Show Password Manager by default + highlight it
        ShowPasswordManager();
    }

    // ================== BUTTON CLICK HANDLERS ==================

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new EntryWindow();
        if (dialog.ShowDialog() == true)
        {
            _vault.Entries.Add(dialog.Entry);
            VaultStore.Save(_vault, _password);
            VaultList.Items.Refresh();
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (VaultList.SelectedItem is VaultEntry entry)
        {
            var dialog = new EntryWindow(entry);
            if (dialog.ShowDialog() == true)
            {
                VaultStore.Save(_vault, _password);
                VaultList.Items.Refresh();
            }
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (VaultList.SelectedItem is VaultEntry v)
        {
            _vault.Entries.Remove(v);
            VaultStore.Save(_vault, _password);
            VaultList.Items.Refresh();
        }
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (VaultList.SelectedItem is VaultEntry v)
        {
            Clipboard.SetText(v.Password);
            await Task.Delay(15000);
            Clipboard.Clear();
        }
    }

    private void Lock_Click(object sender, RoutedEventArgs e) => Lock();

    private void Lock()
    {
        _lockTimer.Stop();
        new LoginWindow().Show();
        Close();
    }

    // ================== CSV IMPORT ==================

    private void ImportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "CSV Files (*.csv)|*.csv"
        };

        if (dialog.ShowDialog() == true)
        {
            ImportFromCsv(dialog.FileName);
            MessageBox.Show("Passwords imported successfully!");
        }
    }

    public void ImportFromCsv(string csvPath)
    {
        var lines = File.ReadAllLines(csvPath);

        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split(',');

            if (parts.Length >= 4)
            {
                var entry = new VaultEntry
                {
                    Title = parts[0],
                    Username = parts[2],
                    Password = parts[3]
                };

                _vault.Entries.Add(entry);
            }
        }

        VaultStore.Save(_vault, _password);
        VaultList.Items.Refresh();

        // If you're currently searching, re-run filter so the view stays consistent
        if (!string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            string filter = SearchBox.Text.ToLower();

            VaultList.ItemsSource = _vault.Entries
                .Where(x =>
                    (x.Title ?? "").ToLower().Contains(filter) ||
                    (x.Username ?? "").ToLower().Contains(filter))
                .ToList();
        }
    }

    // ================== SEARCH ==================

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_vault == null) return;

        string filter = (SearchBox.Text ?? "").ToLower();

        if (string.IsNullOrWhiteSpace(filter))
        {
            VaultList.ItemsSource = _vault.Entries;
            VaultList.Items.Refresh();
            return;
        }

        VaultList.ItemsSource = _vault.Entries
            .Where(x =>
                (x.Title ?? "").ToLower().Contains(filter) ||
                (x.Username ?? "").ToLower().Contains(filter))
            .ToList();
    }

    // ================== TAB SWITCHING ==================

    private void PasswordManager_Click(object sender, RoutedEventArgs e) => ShowPasswordManager();

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private void ShowPasswordManager()
    {
        HeaderText.Text = "Password Manager";
        Toolbar.Visibility = Visibility.Visible;

        PasswordManagerView.Visibility = Visibility.Visible;
        SettingsView.Visibility = Visibility.Collapsed;

        SetActiveTab(BtnPasswordManager);
    }

    private void ShowSettings()
    {
        HeaderText.Text = "Settings";
        Toolbar.Visibility = Visibility.Collapsed;

        PasswordManagerView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;

        SetActiveTab(BtnSettings);
    }

    // ================== SIDEBAR HIGHLIGHTING ==================

    private void SetActiveTab(Button active)
    {
        var inactive = (Style)FindResource("SidebarButtonInactive");
        var activeStyle = (Style)FindResource("SidebarButtonActive");

        BtnPasswordManager.Style = inactive;
        BtnAccounts.Style = inactive;
        BtnNotes.Style = inactive;
        BtnSettings.Style = inactive;

        active.Style = activeStyle;
    }
}
