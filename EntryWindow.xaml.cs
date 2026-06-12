using SecureVault.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SecureVault
{
    public partial class EntryWindow : Window
    {
        public VaultEntry Entry { get; private set; }
        private readonly VaultSettings _settings;

        public EntryWindow(VaultEntry? entry = null, IEnumerable<string>? existingTags = null, VaultSettings? settings = null)
        {
            InitializeComponent();
            Entry = entry ?? new VaultEntry();
            _settings = settings ?? new VaultSettings();
            GeneratorLengthBox.Text = Math.Clamp(_settings.GeneratorLength, 8, 128).ToString();
            UppercaseCheck.IsChecked = _settings.GeneratorUppercase;
            LowercaseCheck.IsChecked = _settings.GeneratorLowercase;
            DigitsCheck.IsChecked = _settings.GeneratorDigits;
            SymbolsCheck.IsChecked = _settings.GeneratorSymbols;

            if (entry != null)
            {
                TitleBox.Text    = entry.Title;
                UsernameBox.Text = entry.Username;
                UrlBox.Text      = entry.Url;
                PasswordBox.Password = entry.Password;
                TagsBox.Text = string.Join(", ", entry.Tags);
                TotpSecretBox.Text = entry.TotpSecret;
            }
            TitleBox.Focus();
            UpdateStrength();
            UpdateTotpPreview();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        { if (e.ChangedButton == MouseButton.Left) DragMove(); }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Entry.Title    = TitleBox.Text;
            Entry.Username = UsernameBox.Text;
            Entry.Url      = UrlBox.Text;
            Entry.Password = PasswordBox.Password;
            Entry.TotpSecret = TotpService.NormalizeSecret(TotpSecretBox.Text);
            Entry.Tags = TagsBox.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t.ToLowerInvariant())
                .Distinct()
                .ToList();
            Entry.UpdatedAt = DateTime.Now;

            if (int.TryParse(GeneratorLengthBox.Text, out var length))
                _settings.GeneratorLength = Math.Clamp(length, 8, 128);
            _settings.GeneratorUppercase = UppercaseCheck.IsChecked == true;
            _settings.GeneratorLowercase = LowercaseCheck.IsChecked == true;
            _settings.GeneratorDigits = DigitsCheck.IsChecked == true;
            _settings.GeneratorSymbols = SymbolsCheck.IsChecked == true;

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void GeneratePassword_Click(object sender, RoutedEventArgs e)
            => PasswordBox.Password = GeneratePassword();

        private void CopyPassword_Click(object sender, RoutedEventArgs e)
        { if (!string.IsNullOrEmpty(PasswordBox.Password)) Clipboard.SetText(PasswordBox.Password); }

        private string GeneratePassword()
        {
            const string u = "ABCDEFGHJKLMNPQRSTUVWXYZ";
            const string l = "abcdefghjkmnpqrstuvwxyz";
            const string d = "23456789";
            const string s = "!@#$%^&*-_=+?";
            var groups = new List<string>();
            if (UppercaseCheck.IsChecked == true) groups.Add(u);
            if (LowercaseCheck.IsChecked == true) groups.Add(l);
            if (DigitsCheck.IsChecked == true) groups.Add(d);
            if (SymbolsCheck.IsChecked == true) groups.Add(s);
            if (groups.Count == 0) groups.Add(l + d);

            var length = int.TryParse(GeneratorLengthBox.Text, out var parsed) ? Math.Clamp(parsed, 8, 128) : 18;
            var all = string.Concat(groups);
            var pw = new List<char>();
            foreach (var group in groups)
                pw.Add(group[RandomNumberGenerator.GetInt32(group.Length)]);
            while (pw.Count < length)
                pw.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);
            Shuffle(pw);
            return new string(pw.ToArray());
        }

        private static void Shuffle(IList<char> chars)
        {
            for (var i = chars.Count - 1; i > 0; i--)
            {
                var j = RandomNumberGenerator.GetInt32(i + 1);
                (chars[i], chars[j]) = (chars[j], chars[i]);
            }
        }

        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e) => UpdateStrength();

        private void TotpSecretBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateTotpPreview();

        private void PasteTotpUri_Click(object sender, RoutedEventArgs e)
        {
            if (Clipboard.ContainsText())
            {
                TotpSecretBox.Text = TotpService.NormalizeSecret(Clipboard.GetText());
                UpdateTotpPreview();
            }
        }

        private void UpdateStrength()
        {
            var score = SecurityInsights.PasswordStrength(PasswordBox.Password);
            StrengthText.Text = $"Strength: {SecurityInsights.StrengthLabel(score)} ({score}%)";
        }

        private void UpdateTotpPreview()
        {
            try
            {
                var secret = TotpService.NormalizeSecret(TotpSecretBox.Text);
                TotpPreviewText.Text = string.IsNullOrWhiteSpace(secret)
                    ? "No 2FA code configured"
                    : $"Current code: {TotpService.GenerateCode(secret)}";
            }
            catch
            {
                TotpPreviewText.Text = "Invalid TOTP secret";
            }
        }
    }
}
