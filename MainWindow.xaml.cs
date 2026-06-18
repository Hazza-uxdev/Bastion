using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using SecureVault.Models;
using SecureVault.Storage;

namespace SecureVault;

public partial class MainWindow : Window
{
    private Vault _vault;
    private string _password;
    private DispatcherTimer _lockTimer;
    private DispatcherTimer _saveTimer;
    private DispatcherTimer _totpTimer;
    private int _lockMinutes = 3;

    private SecureNote? _currentNote;
    private bool _suppressNoteChange = false;
    private bool _suppressNoteTreeSelection = false;
    private string _currentSortMode = "LastEdited";

    // Browser autofill API
    private BastionLocalApi? _localApi;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _allowExit;
    private readonly bool _startHiddenToTray;

    // Tag filter state
    private string? _activeTagFilter = null;
    private string? _activePasswordTagFilter = null;
    private bool _isUpdatingSettingsControls = false;
    private bool _isUpdatingColorText = false;
    private Button? _activeNavButton;
    private readonly Dictionary<DependencyObject, ThemeSnapshot> _themeSnapshots = new();
    private BitmapSource? _colorMapSource;
    private const int MaxAttachmentBytes = 10 * 1024 * 1024;

    // Graph
    private double _graphOffsetX = 0, _graphOffsetY = 0, _graphScale = 1.0;
    private bool _isPanning;
    private Point _panStart;
    private Ellipse? _draggingNode;
    private Point _nodeDragOffset;
    private readonly Dictionary<Ellipse, SecureNote> _nodeMap = new();
    private readonly Dictionary<SecureNote, (double x, double y)> _nodePositions = new();
    // Smooth graph animation state — declared in graph region below

    public MainWindow(Vault vault, string password, bool startHiddenToTray = false)
    {
        InitializeComponent();
        _vault = vault;
        _password = password;
        _startHiddenToTray = startHiddenToTray;

        _lockTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(_lockMinutes) };
        _lockTimer.Tick += (_, _) => Lock();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveCurrentNote(); };

        _totpTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _totpTimer.Tick += (_, _) => RefreshTotpCodes();
        _totpTimer.Start();

        NormalizeVault();
        _lockMinutes = Math.Clamp(_vault.Settings.LockMinutes, 0, 120);
        ApplyLockTimeout(_lockMinutes, save: false);
        LoadGraphColors();
        UpdateSettingsControls();
        VaultList.ItemsSource = _vault.Entries;

        MouseMove += (_, _) => ResetLockTimer();
        KeyDown += (_, _) => ResetLockTimer();

        StartLocalApi();

        ApplyTheme();
        UpdateGreeting();
        ShowHome();
        Loaded += MainWindow_Loaded;
    }

    private void StartLocalApi()
    {
        _localApi?.Stop();
        _localApi = new BastionLocalApi(_vault, () => Dispatcher.Invoke(() =>
        {
            VaultStore.Save(_vault, _password);
            RefreshPasswordList();
            UpdateHomeStats();
        }));
        _localApi.Start();
    }

    private void NormalizeVault()
    {
        _vault.Entries ??= new ObservableCollection<VaultEntry>();
        _vault.Notes ??= new ObservableCollection<SecureNote>();
        _vault.Trash ??= new List<VaultEntry>();
        _vault.NoteTrash ??= new List<SecureNote>();
        _vault.Folders ??= new List<string>();
        _vault.Settings ??= new VaultSettings();
        _vault.Settings.LockMinutes = Math.Clamp(_vault.Settings.LockMinutes, 0, 120);
        _vault.Settings.ClipboardTimeoutSeconds = Math.Clamp(_vault.Settings.ClipboardTimeoutSeconds, 0, 300);
        if (!_vault.Settings.StartOnBoot)
            _vault.Settings.StartHiddenToTray = false;
        _vault.Settings.TagColors ??= new Dictionary<string, string>();
        _vault.Tags ??= new List<string>();
        foreach (var entry in _vault.Entries)
        {
            entry.Tags ??= new List<string>();
            foreach (var tag in entry.Tags)
                if (!_vault.Tags.Contains(tag)) _vault.Tags.Add(tag);
        }
        foreach (var note in _vault.Notes)
        {
            note.Tags ??= new List<string>();
            note.History ??= new List<NoteSnapshot>();
            note.Attachments ??= new List<NoteAttachment>();
            foreach (var tag in note.Tags)
                if (!_vault.Tags.Contains(tag)) _vault.Tags.Add(tag);
        }
    }

    private void ResetLockTimer()
    {
        if (_lockMinutes == 0 || _lockTimer == null) return;
        _lockTimer.Stop(); _lockTimer.Start();
    }

    // ---- TITLEBAR ----
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        { if (e.ClickCount == 2) ToggleMaximize(); else DragMove(); }
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized) { RootBorder.BorderThickness = new Thickness(0); MaxHeight = SystemParameters.WorkArea.Height + 7; }
        else { RootBorder.BorderThickness = new Thickness(1); MaxHeight = double.PositiveInfinity; }
        if (WindowState == WindowState.Minimized && _vault?.Settings?.RunInTray == true)
            Dispatcher.BeginInvoke(new Action(() => HideToTray("Bastion is still running in the tray.")), DispatcherPriority.Background);
    }
    private void CloseApp_Click(object sender, RoutedEventArgs e) { SaveCurrentNote(); Close(); }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_vault.Settings.RunInTray || _startHiddenToTray)
            EnsureTrayIcon();
        if (_startHiddenToTray)
            HideToTray("Bastion unlocked. Browser autofill is available while the vault stays unlocked.");
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon != null) return;

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Bastion", null, (_, _) => ShowFromTray());
        menu.Items.Add("Lock vault", null, (_, _) => Dispatcher.Invoke(Lock));
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitFromTray));

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath)
                   ?? System.Drawing.SystemIcons.Application,
            Text = "Bastion vault unlocked",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }

    private void HideToTray(string? balloonText = null)
    {
        EnsureTrayIcon();
        Hide();
        if (!string.IsNullOrWhiteSpace(balloonText))
            _trayIcon?.ShowBalloonTip(2500, "Bastion", balloonText, System.Windows.Forms.ToolTipIcon.Info);
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _allowExit = true;
        SaveCurrentNote();
        _localApi?.Stop();
        _trayIcon?.Dispose();
        _trayIcon = null;
        Application.Current.Shutdown();
    }

    // ---- HOME ----
    private void UpdateGreeting() { var h = DateTime.Now.Hour; HomeGreeting.Text = h < 12 ? "Good morning" : h < 17 ? "Good afternoon" : "Good evening"; }
    private void Home_Click(object sender, RoutedEventArgs e) => ShowHome();
    private void ShowHome() { UpdateHomeStats(); SetView(HomeView); SetActiveTab(BtnHome); }
    private void UpdateHomeStats()
    {
        PasswordCountText.Text = (_vault?.Entries?.Count ?? 0).ToString();
        NoteCountText.Text = (_vault?.Notes?.Count ?? 0).ToString();
        RecentNotesList.ItemsSource = _vault?.Notes?.OrderByDescending(n => n.UpdatedAt).Take(8).ToList();
    }
    private void QuickNewNote_Click(object sender, RoutedEventArgs e) { ShowNotes(); CreateNewNote(); }
    private void RecentNotes_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecentNotesList.SelectedItem is SecureNote note) { ShowNotes(); OpenNote(note); SelectNoteInTree(note); }
    }

    // ---- NAV ----
    private void PasswordManager_Click(object sender, RoutedEventArgs e) => ShowPasswordManager();
    private void Notes_Click(object sender, RoutedEventArgs e) => ShowNotes();
    private void Graph_Click(object sender, RoutedEventArgs e) => ShowGraph();
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    private void Security_Click(object sender, RoutedEventArgs e) { SetView(SecurityView); SetActiveTab(BtnSecurity); RunSecurityCheck(); }
    private void Trash_Click(object sender, RoutedEventArgs e) { SetView(TrashView); SetActiveTab(BtnTrash); RefreshTrash(); }

    private void SetView(UIElement v)
    {
        HomeView.Visibility = PasswordManagerView.Visibility = NotesView.Visibility =
        GraphView.Visibility = SettingsView.Visibility =
        SecurityView.Visibility = TrashView.Visibility = Visibility.Collapsed;
        v.Visibility = Visibility.Visible;
        _activeNavButton = GetNavButtonForView(v);
        ApplyNavigationTheme();
        if (_vault?.Settings != null)
            Dispatcher.BeginInvoke(new Action(ApplyTheme), DispatcherPriority.Loaded);
    }
    private void ShowPasswordManager() { SetView(PasswordManagerView); SetActiveTab(BtnPasswordManager); RefreshPasswordList(); }
    private void ShowSettings() { SetView(SettingsView); SetActiveTab(BtnSettings); }
    private void ShowNotes() { SetView(NotesView); SetActiveTab(BtnNotes); RefreshNotesTree(); RefreshTagPanel(); }
    private void ShowGraph() { SetView(GraphView); SetActiveTab(BtnGraph); BuildGraph(); }

    private void SetActiveTab(Button active)
    {
        var off = (Style)FindResource("NavButton");
        BtnHome.Style = BtnPasswordManager.Style = BtnNotes.Style = BtnGraph.Style =
        BtnSettings.Style = BtnSecurity.Style = BtnTrash.Style = off;
        _activeNavButton = active;
        ApplyNavigationTheme();
    }

    private Button? GetNavButtonForView(UIElement view)
    {
        if (view == HomeView) return BtnHome;
        if (view == PasswordManagerView) return BtnPasswordManager;
        if (view == NotesView) return BtnNotes;
        if (view == GraphView) return BtnGraph;
        if (view == SecurityView) return BtnSecurity;
        if (view == TrashView) return BtnTrash;
        if (view == SettingsView) return BtnSettings;
        return _activeNavButton;
    }

    // ---- LOCK ----
    private void Lock_Click(object sender, RoutedEventArgs e) => Lock();
    private void Lock()
    {
        SaveCurrentNote();
        _lockTimer.Stop();
        _totpTimer.Stop();
        CompositionTarget.Rendering -= GraphRenderFrame;
        _localApi?.Stop();
        _trayIcon?.Dispose();
        _trayIcon = null;
        new LoginWindow().Show();
        _allowExit = true;
        Close();
    }
    private void LockTimeout_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_lockTimer == null || _isUpdatingSettingsControls) return;
        if (LockTimeoutCombo.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int mins))
            ApplyLockTimeout(mins, save: true);
    }

    private void ApplyLockTimeout(int minutes, bool save)
    {
        _lockMinutes = Math.Clamp(minutes, 0, 120);
        _lockTimer.Stop();
        if (_lockMinutes > 0)
        {
            _lockTimer.Interval = TimeSpan.FromMinutes(_lockMinutes);
            _lockTimer.Start();
        }

        if (_vault?.Settings != null)
        {
            _vault.Settings.LockMinutes = _lockMinutes;
            if (save)
                VaultStore.Save(_vault, _password);
        }
    }

    // ---- PASSWORDS ----
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var d = new EntryWindow(null, _vault.Tags, _vault.Settings);
        if (d.ShowDialog() == true) { _vault.Entries.Add(d.Entry); SyncVaultTags(); VaultStore.Save(_vault, _password); RefreshPasswordList(); UpdateHomeStats(); }
    }
    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (VaultList.SelectedItem is VaultEntry entry) { var d = new EntryWindow(entry, _vault.Tags, _vault.Settings); if (d.ShowDialog() == true) { SyncVaultTags(); VaultStore.Save(_vault, _password); RefreshPasswordList(); } }
    }
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (VaultList.SelectedItem is VaultEntry v)
        {
            var dlg = new BastionDialog($"Delete \"{v.Title}\"?", "This will be moved to Trash. You can restore it later.", true);
            if (dlg.ShowDialog() == true)
            {
                _vault.Entries.Remove(v);
                _vault.Trash.Add(v);
                VaultStore.Save(_vault, _password);
                RefreshPasswordList(); UpdateHomeStats();
            }
        }
    }
    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (VaultList.SelectedItem is VaultEntry v)
            await CopySecretToClipboardAsync(v.Password, "Password");
    }

    private async void CopyTotp_Click(object sender, RoutedEventArgs e)
    {
        if (VaultList.SelectedItem is not VaultEntry v || !v.HasTotp)
        {
            new BastionDialog("No 2FA code", "Select a password entry with a TOTP secret first.", false).ShowDialog();
            return;
        }

        await CopySecretToClipboardAsync(v.TotpCode, "2FA code");
    }

    private async Task CopySecretToClipboardAsync(string value, string label)
    {
        if (string.IsNullOrEmpty(value)) return;

        Clipboard.SetText(value);
        var timeout = Math.Clamp(_vault?.Settings?.ClipboardTimeoutSeconds ?? 15, 0, 300);
        if (timeout <= 0)
        {
            NoteSaveStatus.Text = $"{label} copied.";
            return;
        }

        NoteSaveStatus.Text = $"{label} copied - clears in {timeout}s";
        await Task.Delay(TimeSpan.FromSeconds(timeout));
        if (Clipboard.ContainsText() && Clipboard.GetText() == value)
        {
            Clipboard.Clear();
            NoteSaveStatus.Text = "Clipboard cleared.";
        }
    }

    private void RefreshTotpCodes()
    {
        if (VaultList == null || !PasswordManagerView.IsVisible) return;
        if (_vault.Entries.Any(e => e.HasTotp))
            VaultList.Items.Refresh();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshPasswordList();
    }

    private void RefreshPasswordList()
    {
        if (_vault == null || VaultList == null) return;
        IEnumerable<VaultEntry> entries = _vault.Entries;
        var filter = (SearchBox?.Text ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            entries = FuzzySearch.Search(entries, filter,
                e => $"{e.Title} {e.Username} {e.Url} {string.Join(" ", e.Tags)}", 100);
        }
        if (!string.IsNullOrEmpty(_activePasswordTagFilter))
            entries = entries.Where(e => e.Tags.Contains(_activePasswordTagFilter));
        VaultList.ItemsSource = entries.ToList();
        RefreshPasswordTagsPanel();
    }

    private void SyncVaultTags()
    {
        var tags = _vault.Notes.SelectMany(n => n.Tags)
            .Concat(_vault.Entries.SelectMany(e => e.Tags))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .Distinct()
            .OrderBy(t => t)
            .ToList();
        _vault.Tags = tags;
    }

    private void RefreshPasswordTagsPanel()
    {
        if (PasswordTagsPanel == null) return;
        PasswordTagsPanel.Children.Clear();
        foreach (var tag in _vault.Entries.SelectMany(e => e.Tags).Distinct().OrderBy(t => t))
        {
            var chip = BuildTagChip(tag, () =>
            {
                foreach (var entry in _vault.Entries)
                    entry.Tags.Remove(tag);
                SyncVaultTags();
                VaultStore.Save(_vault, _password);
                RefreshPasswordList();
            }, () =>
            {
                _activePasswordTagFilter = _activePasswordTagFilter == tag ? null : tag;
                RefreshPasswordList();
            });
            chip.Background = _activePasswordTagFilter == tag
                ? new SolidColorBrush(BlendColor(Color.FromRgb(0x16, 0x16, 0x16), GetTagColor(tag), 0.36))
                : new SolidColorBrush(BlendColor(Color.FromRgb(0x22, 0x22, 0x22), GetTagColor(tag), 0.16));
            PasswordTagsPanel.Children.Add(chip);
        }
    }
    private void ImportCsv_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Filter = "CSV Files (*.csv)|*.csv" };
        if (d.ShowDialog() == true)
        {
            var rows = File.ReadLines(d.FileName)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(ParseCsvLine)
                .ToList();
            if (rows.Count == 0)
            {
                new BastionDialog("Import failed", "The selected CSV file did not contain any rows.", false).ShowDialog();
                return;
            }

            var headers = rows[0].Select(NormalizeCsvHeader).ToList();
            var hasHeader = headers.Any(h => h is "title" or "name" or "url" or "website" or "username" or "login" or "password");
            var imported = 0;
            var skipped = 0;

            foreach (var row in hasHeader ? rows.Skip(1) : rows)
            {
                if (TryCreateEntryFromCsv(row, hasHeader ? headers : null, out var entry))
                {
                    _vault.Entries.Add(entry);
                    imported++;
                }
                else
                {
                    skipped++;
                }
            }

            SyncVaultTags();
            VaultStore.Save(_vault, _password);
            RefreshPasswordList();
            UpdateHomeStats();
            var skippedText = skipped > 0 ? $" Skipped {skipped} incomplete row(s)." : "";
            new BastionDialog("Import complete", $"Imported {imported} password(s).{skippedText}", false).ShowDialog();
        }
    }

    private static bool TryCreateEntryFromCsv(IReadOnlyList<string> row, IReadOnlyList<string>? headers, out VaultEntry entry)
    {
        string Field(params string[] names)
        {
            if (headers == null) return "";
            foreach (var name in names)
            {
                var normalized = NormalizeCsvHeader(name);
                for (var index = 0; index < headers.Count; index++)
                    if (headers[index] == normalized && index < row.Count)
                        return row[index].Trim();
            }
            return "";
        }

        var title = headers == null ? GetColumn(row, 0) : Field("title", "name", "site");
        var url = headers == null ? GetColumn(row, 1) : Field("url", "website", "uri", "loginurl");
        var username = headers == null ? GetColumn(row, 2) : Field("username", "user", "login", "email");
        var password = headers == null ? GetColumn(row, 3) : Field("password", "pass");
        var tags = headers == null ? "" : Field("tags", "tag", "group", "folder");
        var totp = headers == null ? "" : Field("totp", "totpsecret", "otp", "2fa");

        entry = new VaultEntry();
        if (string.IsNullOrWhiteSpace(password))
            return false;

        entry.Title = string.IsNullOrWhiteSpace(title) ? url : title;
        entry.Url = url;
        entry.Username = username;
        entry.Password = password;
        entry.TotpSecret = TotpService.NormalizeSecret(totp);
        entry.Tags = tags.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Distinct()
            .ToList();
        entry.CreatedAt = DateTime.Now;
        entry.UpdatedAt = DateTime.Now;
        return true;
    }

    private static string GetColumn(IReadOnlyList<string> row, int index)
        => index >= 0 && index < row.Count ? row[index].Trim() : "";

    private static string NormalizeCsvHeader(string value)
        => new((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        values.Add(current.ToString());
        return values;
    }

    // ---- NOTES TREE ----

    private void RefreshNotesTree()
    {
        if (NotesTree == null) return;
        NotesTree.Items.Clear();

        var notes = GetSortedNotes();
        var filter = (NotesSearchBox?.Text ?? "").Trim().ToLower();
        if (!string.IsNullOrEmpty(filter))
            notes = notes.Where(n => (n.Title ?? "").ToLower().Contains(filter) || (n.Body ?? "").ToLower().Contains(filter)).ToList();

        // Tag filter
        if (!string.IsNullOrEmpty(_activeTagFilter))
            notes = notes.Where(n => n.Tags.Contains(_activeTagFilter)).ToList();

        var folders = notes.Where(n => !string.IsNullOrEmpty(n.Folder))
                           .GroupBy(n => n.Folder).OrderBy(g => g.Key)
                           .ToDictionary(g => g.Key, g => g.ToList());
        var unfoldered = notes.Where(n => string.IsNullOrEmpty(n.Folder)).ToList();

        foreach (var kv in folders)
        {
            NotesTree.Items.Add(BuildFolderHeader(kv.Key));
            foreach (var note in kv.Value)
                NotesTree.Items.Add(BuildNoteListItem(note, indent: true));
        }
        foreach (var note in unfoldered)
            NotesTree.Items.Add(BuildNoteListItem(note, indent: false));
    }

    private ListBoxItem BuildFolderHeader(string name)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        sp.Children.Add(new TextBlock { Text = "📁 ", FontSize = 11 });
        sp.Children.Add(new TextBlock { Text = name, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)) });
        return new ListBoxItem { Content = sp, IsEnabled = false, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(6, 3, 6, 3) };
    }

    private ListBoxItem BuildNoteListItem(SecureNote note, bool indent)
    {
        var sp = new StackPanel { Margin = new Thickness(indent ? 12 : 0, 1, 0, 1) };
        sp.Children.Add(new TextBlock { Text = (note.IsPinned ? "📌 " : "") + note.Title, FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)), TextTrimming = TextTrimming.CharacterEllipsis });
        sp.Children.Add(new TextBlock { Text = note.UpdatedAt.ToString("MMM d"), FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), Margin = new Thickness(0, 1, 0, 0) });

        var item = new ListBoxItem { Content = sp, Tag = note, Padding = new Thickness(8, 6, 8, 6) };

        // Right-click context menu: Move to folder
        var ctx = CreateBastionContextMenu();

        var moveHeader = CreateBastionMenuItem("Move to folder", isEnabled: false);
        ctx.Items.Add(moveHeader);
        ctx.Items.Add(new Separator());

        // No folder option
        var noFolder = CreateBastionMenuItem("(No folder)");
        noFolder.Click += (_, _) => { note.Folder = ""; VaultStore.Save(_vault, _password); RefreshNotesTree(); };
        ctx.Items.Add(noFolder);

        // Existing folders
        foreach (var folder in _vault.Notes.Where(n => !string.IsNullOrEmpty(n.Folder)).Select(n => n.Folder).Distinct().OrderBy(f => f))
        {
            var f = folder;
            var mi = new MenuItem { Header = "📁 " + f, Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), Background = Brushes.Transparent };
            mi.Header = "Folder: " + f;
            mi.MinWidth = 150;
            if (TryFindResource("BastionMenuItem") is Style folderStyle) mi.Style = folderStyle;
            mi.Click += (_, _) => { note.Folder = f; VaultStore.Save(_vault, _password); RefreshNotesTree(); };
            ctx.Items.Add(mi);
        }

        // New folder option
        ctx.Items.Add(new Separator());
        var newFolderMi = new MenuItem { Header = "+ New folder…", Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED)), Background = Brushes.Transparent };
        newFolderMi.Header = "+ New folder...";
        newFolderMi.MinWidth = 150;
        if (TryFindResource("BastionMenuItem") is Style newFolderStyle) newFolderMi.Style = newFolderStyle;
        newFolderMi.Click += (_, _) =>
        {
            var dlg = new FolderNameDialog { Owner = Application.Current.MainWindow };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            { note.Folder = dlg.FolderName; VaultStore.Save(_vault, _password); RefreshNotesTree(); }
        };
        ctx.Items.Add(newFolderMi);

        ctx.Items.Add(new Separator());
        var deleteNoteMi = CreateBastionMenuItem("Delete note", foregroundHex: "#F87171");
        deleteNoteMi.Click += (_, _) => DeleteNote(note);
        ctx.Items.Add(deleteNoteMi);

        item.ContextMenu = ctx;
        return item;
    }

    private ContextMenu CreateBastionContextMenu()
    {
        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4)
        };
        if (TryFindResource("BastionContextMenu") is Style style)
            menu.Style = style;
        return menu;
    }

    private MenuItem CreateBastionMenuItem(string header, bool isEnabled = true, string foregroundHex = "#CCCCCC")
    {
        var item = new MenuItem
        {
            Header = header,
            IsEnabled = isEnabled,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(foregroundHex)),
            Background = Brushes.Transparent
        };
        if (TryFindResource("BastionMenuItem") is Style style)
            item.Style = style;
        return item;
    }

    private void NotesTree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || NotesTree.SelectedItem is not ListBoxItem { Tag: SecureNote note }) return;
        DragDrop.DoDragDrop(NotesTree, note, DragDropEffects.Move);
    }

    private void NotesTree_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(SecureNote))) return;
        var dragged = (SecureNote)e.Data.GetData(typeof(SecureNote))!;
        var targetItem = ItemsControl.ContainerFromElement(NotesTree, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (targetItem?.Tag is not SecureNote target || dragged == target) return;

        _vault.Notes.Remove(dragged);
        var index = _vault.Notes.IndexOf(target);
        if (index < 0) _vault.Notes.Add(dragged);
        else _vault.Notes.Insert(index, dragged);
        _currentSortMode = "Manual";
        VaultStore.Save(_vault, _password);
        RefreshNotesTree();
        SelectNoteInTree(dragged);
    }

    private void RefreshTagPanel()
    {
        if (AllTagsPanel == null) return;
        RefreshAllTags();
        TagFilterBorder.Visibility = _vault.Tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NotesTree_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressNoteTreeSelection) return;
        if (NotesTree.SelectedItem is ListBoxItem { Tag: SecureNote note })
            OpenNote(note);
    }

    private void RefreshNotesTreeKeepingSelection()
    {
        var selectedNote = _currentNote;
        _suppressNoteTreeSelection = true;
        try
        {
            RefreshNotesTree();
            if (selectedNote != null)
                SelectNoteInTree(selectedNote);
        }
        finally
        {
            _suppressNoteTreeSelection = false;
        }
    }

    private void SelectNoteInTree(SecureNote note)
    {
        foreach (ListBoxItem item in NotesTree.Items.OfType<ListBoxItem>())
        {
            if (item.Tag is SecureNote n && n == note) { item.IsSelected = true; return; }
        }
    }

    private List<SecureNote> GetSortedNotes()
    {
        // Pinned always float to top within each sort
        var pinned   = _vault.Notes.Where(n => n.IsPinned);
        var unpinned = _vault.Notes.Where(n => !n.IsPinned);

        IEnumerable<SecureNote> Sort(IEnumerable<SecureNote> src) => _currentSortMode switch
        {
            "CreatedDate"    => src.OrderByDescending(n => n.CreatedAt),
            "Alpha"          => src.OrderBy(n => n.Title),
            "Manual"         => src,
            "LastEditedAsc"  => src.OrderBy(n => n.UpdatedAt),
            "CreatedDateAsc" => src.OrderBy(n => n.CreatedAt),
            "AlphaDesc"      => src.OrderByDescending(n => n.Title),
            _                => src.OrderByDescending(n => n.UpdatedAt)
        };

        return Sort(pinned).Concat(Sort(unpinned)).ToList();
    }

    private void SortCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SortCombo == null || SortCombo.SelectedIndex < 0 || NotesTree == null) return;
        _currentSortMode = SortCombo.SelectedIndex switch
        {
            0 => "LastEdited", 1 => "CreatedDate", 2 => "Alpha",
            3 => "LastEditedAsc", 4 => "CreatedDateAsc", 5 => "AlphaDesc", _ => "LastEdited"
        };
        RefreshNotesTree();
    }

    private void NotesSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (NotesTree == null) return;
        RefreshNotesTree();
    }

    // New folder
    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new FolderNameDialog();
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            // Create a placeholder note in the folder so it appears
            var note = new SecureNote { Title = "New note", Body = "", Folder = dlg.FolderName, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
            _vault.Notes.Add(note);
            VaultStore.Save(_vault, _password);
            RefreshNotesTree();
            OpenNote(note);
            SelectNoteInTree(note);
        }
    }

    private void NewNote_Click(object sender, RoutedEventArgs e) => CreateNewNote();

    private void CreateNewNote()
    {
        SaveCurrentNote();
        var note = new SecureNote { Title = "Untitled", Body = "", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        _vault.Notes.Add(note);
        VaultStore.Save(_vault, _password);
        RefreshNotesTree();
        OpenNote(note);
        SelectNoteInTree(note);
        NoteTitleEditor.Focus(); NoteTitleEditor.SelectAll();
    }

    private void OpenNote(SecureNote note)
    {
        _suppressNoteChange = true;
        _currentNote = note;
        NoteTitleEditor.Text = note.Title;
        NoteBodyEditor.Text = StripAttachmentLinks(note.Body);
        NoteEditorTitle.Text = note.Title;
        NoteCreatedText.Text = note.CreatedAt.ToString("MMM d, yyyy");
        NoteModifiedText.Text = note.UpdatedAt.ToString("MMM d, yyyy HH:mm");
        NoteFolderText.Text = string.IsNullOrEmpty(note.Folder) ? "—" : note.Folder;
        UpdateWordCount(); UpdateOutline();
        RefreshNoteTags();
        RefreshAttachments();
        _suppressNoteChange = false;
    }

    private void NoteContent_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppressNoteChange || _currentNote == null) return;
        NoteEditorTitle.Text = NoteTitleEditor.Text;
        UpdateWordCount(); UpdateOutline();
        _saveTimer.Stop(); _saveTimer.Start();
        NoteSaveStatus.Text = "Unsaved changes...";
    }

    private void SaveCurrentNote()
    {
        if (_currentNote == null) return;

        // Take snapshot if body changed significantly (>10 chars diff)
        var oldTitle = _currentNote.Title ?? "";
        var oldBody = _currentNote.Body ?? "";
        var newBody = StripAttachmentLinks(NoteBodyEditor.Text ?? "");
        if (Math.Abs(newBody.Length - oldBody.Length) > 10 || (_currentNote.History.Count == 0 && newBody.Length > 0))
        {
            _currentNote.History.Add(new NoteSnapshot
            {
                SavedAt = DateTime.Now,
                Body = "",
                CompressedBodyBase64 = CompressText(oldBody),
                Title = _currentNote.Title ?? "",
                IsFullCopy = true
            });
            // Keep last 50 snapshots
            if (_currentNote.History.Count > 50)
                _currentNote.History.RemoveAt(0);
        }

        _currentNote.Title = NoteTitleEditor.Text.Trim().Length > 0 ? NoteTitleEditor.Text.Trim() : "Untitled";
        _currentNote.Body = newBody;
        if (NoteBodyEditor.Text != newBody)
        {
            var selectionStart = NoteBodyEditor.SelectionStart;
            var selectionLength = NoteBodyEditor.SelectionLength;
            _suppressNoteChange = true;
            NoteBodyEditor.Text = newBody;
            var safeStart = Math.Min(selectionStart, NoteBodyEditor.Text.Length);
            var safeLength = Math.Min(selectionLength, Math.Max(0, NoteBodyEditor.Text.Length - safeStart));
            NoteBodyEditor.Select(safeStart, safeLength);
            _suppressNoteChange = false;
        }
        _currentNote.UpdatedAt = DateTime.Now;
        VaultStore.Save(_vault, _password);
        if (NotesTree != null && !string.Equals(oldTitle, _currentNote.Title, StringComparison.Ordinal))
            RefreshNotesTreeKeepingSelection();
        UpdateHomeStats();
        UpdateBacklinks();
        NoteSaveStatus.Text = $"Saved · {DateTime.Now:HH:mm:ss}";
        NoteModifiedText.Text = _currentNote.UpdatedAt.ToString("MMM d, yyyy HH:mm");
    }

    private void DeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNote == null) return;
        DeleteNote(_currentNote);
        /*
        return;
        var dlg = new BastionDialog($"Delete \"{_currentNote.Title}\"?", "This note will be moved to Trash.", true);
        if (dlg.ShowDialog() == true)
        {
            _vault.Notes.Remove(_currentNote);
            _vault.NoteTrash.Add(_currentNote);
            _currentNote = null;
            VaultStore.Save(_vault, _password);
            RefreshNotesTree(); UpdateHomeStats();
            _suppressNoteChange = true;
            NoteTitleEditor.Text = ""; NoteBodyEditor.Text = "";
            NoteEditorTitle.Text = "Select or create a note";
            NoteCreatedText.Text = NoteModifiedText.Text = NoteWordCountPanel.Text = "—";
            NoteFolderText.Text = "—"; OutlineList.ItemsSource = null; BacklinksList.ItemsSource = null;
            _suppressNoteChange = false;
        }
    }

        */
    }

    private void DeleteNote(SecureNote note)
    {
        var dlg = new BastionDialog($"Delete \"{note.Title}\"?", "This note will be moved to Trash.", true) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var wasCurrent = ReferenceEquals(_currentNote, note);
        _vault.Notes.Remove(note);
        _vault.NoteTrash.Add(note);
        if (wasCurrent) _currentNote = null;

        VaultStore.Save(_vault, _password);
        RefreshNotesTree();
        UpdateHomeStats();
        if (wasCurrent) ClearCurrentNoteUi();
    }

    private void ClearCurrentNoteUi()
    {
        _suppressNoteChange = true;
        NoteTitleEditor.Text = "";
        NoteBodyEditor.Text = "";
        NoteEditorTitle.Text = "Select or create a note";
        NoteCreatedText.Text = NoteModifiedText.Text = NoteWordCountPanel.Text = "—";
        NoteFolderText.Text = "—";
        NoteWordCount.Text = "0 words";
        NoteTagsPanel.Children.Clear();
        AttachmentsList.ItemsSource = null;
        InlineAttachmentsPanel.Children.Clear();
        InlineAttachmentsHost.Visibility = Visibility.Collapsed;
        OutlineList.ItemsSource = null;
        BacklinksList.ItemsSource = null;
        _suppressNoteChange = false;
    }

    private static string StripAttachmentLinks(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var cleaned = Regex.Replace(
            text,
            @"^\s*\[attachment:[^\]]+\]\(bastion-attachment://[A-Za-z0-9-]+\)\s*$\r?\n?",
            "",
            RegexOptions.Multiline);
        return cleaned;
    }

    private void UpdateWordCount()
    {
        var w = NoteBodyEditor.Text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
        NoteWordCount.Text = $"{w} words"; NoteWordCountPanel.Text = w.ToString();
    }

    private void UpdateOutline()
    {
        var headers = NoteBodyEditor.Text.Split('\n')
            .Where(l => l.StartsWith("#"))
            .Select(l => new string(' ', (l.TakeWhile(c => c == '#').Count() - 1) * 2) + l.TrimStart('#').Trim())
            .ToList();
        OutlineList.ItemsSource = headers.Count > 0 ? (IEnumerable<string>)headers : new List<string> { "No headings" };
    }

    private void OutlineList_Click(object sender, SelectionChangedEventArgs e) { NoteBodyEditor.Focus(); }

    private void RenderPreview() => RefreshInlineAttachments();

    private void RenderMarkdownToRichText(string md, FlowDocument doc)
    {
        bool inCode = false;
        foreach (var rawLine in md.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("```")) { inCode = !inCode; continue; }
            if (inCode) { doc.Blocks.Add(new Paragraph(new Run(line)) { Background = new SolidColorBrush(Color.FromRgb(0x1C,0x1C,0x1C)), Foreground = new SolidColorBrush(Color.FromRgb(0xA8,0xCC,0x8C)), FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0,1,0,1), Padding = new Thickness(12,2,12,2) }); continue; }
            if (line.StartsWith("# ")) { doc.Blocks.Add(MakePara(line[2..], 26, FontWeights.Bold, "#E8E8E8", 16, 4)); continue; }
            if (line.StartsWith("## ")) { doc.Blocks.Add(MakePara(line[3..], 20, FontWeights.SemiBold, "#E8E8E8", 12, 2)); continue; }
            if (line.StartsWith("### ")) { doc.Blocks.Add(MakePara(line[4..], 16, FontWeights.SemiBold, "#CCCCCC", 8, 2)); continue; }
            var attachmentMatch = Regex.Match(line, @"bastion-attachment://([A-Za-z0-9-]+)");
            if (attachmentMatch.Success && TryAddAttachmentPreview(doc, attachmentMatch.Groups[1].Value)) continue;
            if (line.TrimStart('-').Trim().Length == 0 && line.Length >= 3) { doc.Blocks.Add(new BlockUIContainer(new Rectangle { Height = 1, Fill = new SolidColorBrush(Color.FromRgb(0x2D,0x2D,0x2D)), Margin = new Thickness(0,8,0,8) })); continue; }
            if (line.StartsWith("- ") || line.StartsWith("* ")) { var p = new Paragraph { Margin = new Thickness(16,1,0,1) }; p.Inlines.Add(new Run("• ") { Foreground = new SolidColorBrush(Color.FromRgb(0x7C,0x3A,0xED)) }); AddInline(p, line[2..]); doc.Blocks.Add(p); continue; }
            if (Regex.IsMatch(line, @"^\d+\. ")) { var p = new Paragraph { Margin = new Thickness(16,1,0,1) }; AddInline(p, line); doc.Blocks.Add(p); continue; }
            if (line.StartsWith("> ")) { var p = new Paragraph { Margin = new Thickness(12,2,0,2), Padding = new Thickness(10,4,0,4), BorderBrush = new SolidColorBrush(Color.FromRgb(0x7C,0x3A,0xED)), BorderThickness = new Thickness(3,0,0,0), Foreground = new SolidColorBrush(Color.FromRgb(0x88,0x88,0x88)) }; AddInline(p, line[2..]); doc.Blocks.Add(p); continue; }
            if (string.IsNullOrWhiteSpace(line)) { doc.Blocks.Add(new Paragraph { Margin = new Thickness(0,2,0,2) }); continue; }
            var para = new Paragraph { Margin = new Thickness(0,2,0,2) }; AddInline(para, line); doc.Blocks.Add(para);
        }
    }

    private static Paragraph MakePara(string t, double sz, FontWeight w, string hex, double top, double bot) =>
        new Paragraph(new Run(t)) { FontSize = sz, FontWeight = w, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)), Margin = new Thickness(0, top, 0, bot) };

    private bool TryAddAttachmentPreview(FlowDocument doc, string id)
    {
        var attachment = _currentNote?.Attachments.FirstOrDefault(a => a.Id == id);
        if (attachment == null) return false;
        if (attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var bytes = Convert.FromBase64String(attachment.DataBase64);
                var image = new BitmapImage();
                using var stream = new MemoryStream(bytes);
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();

                var panel = new StackPanel();
                panel.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x12)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(6),
                    Child = new Image
                    {
                        Source = image,
                        MaxWidth = 520,
                        MaxHeight = 360,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Left
                    }
                });
                panel.Children.Add(new TextBlock
                {
                    Text = attachment.FileName,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    FontSize = 11,
                    Margin = new Thickness(0, 4, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                doc.Blocks.Add(new BlockUIContainer(panel) { Margin = new Thickness(0, 8, 0, 12) });
                return true;
            }
            catch
            {
                doc.Blocks.Add(MakePara($"Image attachment could not be previewed: {attachment.FileName}", 13, FontWeights.Normal, "#AAAAAA", 4, 4));
                return true;
            }
        }

        doc.Blocks.Add(MakePara($"Attachment: {attachment.FileName} ({FormatFileSize(GetAttachmentSizeBytes(attachment))})", 13, FontWeights.Normal, "#AAAAAA", 4, 4));
        return true;
    }

    private static void AddInline(Paragraph p, string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            var m = Regex.Match(text[i..], @"(\[\[(.+?)\]\])|(\*\*(.+?)\*\*)|(\*(.+?)\*)|(`(.+?)`)");
            if (!m.Success) { p.Inlines.Add(new Run(text[i..]) { Foreground = new SolidColorBrush(Color.FromRgb(0xCC,0xCC,0xCC)) }); break; }
            if (m.Index > 0) p.Inlines.Add(new Run(text.Substring(i, m.Index)) { Foreground = new SolidColorBrush(Color.FromRgb(0xCC,0xCC,0xCC)) });
            if (m.Value.StartsWith("[[")) p.Inlines.Add(new Run(m.Groups[2].Value) { Foreground = new SolidColorBrush(Color.FromRgb(0x9D,0x7C,0xFF)), TextDecorations = TextDecorations.Underline });
            else if (m.Value.StartsWith("**")) p.Inlines.Add(new Bold(new Run(m.Groups[4].Value)) { Foreground = new SolidColorBrush(Color.FromRgb(0xE8,0xE8,0xE8)) });
            else if (m.Value.StartsWith("*")) p.Inlines.Add(new Italic(new Run(m.Groups[6].Value)) { Foreground = new SolidColorBrush(Color.FromRgb(0xCC,0xCC,0xCC)) });
            else p.Inlines.Add(new Run(m.Groups[8].Value) { FontFamily = new FontFamily("Consolas"), Background = new SolidColorBrush(Color.FromRgb(0x1E,0x1E,0x1E)), Foreground = new SolidColorBrush(Color.FromRgb(0xA8,0xCC,0x8C)) });
            i += m.Index + m.Length;
        }
        if (p.Inlines.Count == 0) p.Inlines.Add(new Run(text) { Foreground = new SolidColorBrush(Color.FromRgb(0xCC,0xCC,0xCC)) });
    }


    // ---- GRAPH ----

    private readonly Dictionary<SecureNote, (Ellipse el, TextBlock lbl, double r, Color fill)> _nodeVisuals = new();
    private double _renderX, _renderY, _renderScale = 1.0;
    private DateTime _lastRenderTime = DateTime.UtcNow;

    // Drag inertia
    private double _dragVx, _dragVy;
    private SecureNote? _draggedNote;

    // Graph colors (defaults, overridable from settings)
    public Color GraphNodeColor { get; set; } = Color.FromRgb(0xEE, 0x00, 0xAA);
    public Color GraphHubColor  { get; set; } = Color.FromRgb(0xFF, 0x00, 0xCC);
    public Color GraphLineColor { get; set; } = Color.FromArgb(140, 0, 200, 220);

    private sealed record GraphLink(SecureNote A, SecureNote B, double Score, bool Explicit);

    private void BuildGraph()
    {
        // Stop old render hook
        CompositionTarget.Rendering -= GraphRenderFrame;
        GraphInnerCanvas.Children.Clear();
        _nodeMap.Clear();
        _nodeVisuals.Clear();

        var notes = _vault.Notes.ToList();
        if (notes.Count == 0) return;

        double cx = GraphCanvas.ActualWidth  > 10 ? GraphCanvas.ActualWidth  / 2 : 500;
        double cy = GraphCanvas.ActualHeight > 10 ? GraphCanvas.ActualHeight / 2 : 320;

        var links = BuildGraphLinks(notes);
        var connCount = notes.ToDictionary(n => n, _ => 0);
        foreach (var link in links)
        {
            connCount[link.A]++;
            connCount[link.B]++;
        }

        // Initial circle positions with jitter
        var rng = new Random(42);
        for (int i = 0; i < notes.Count; i++)
        {
            if (_nodePositions.ContainsKey(notes[i])) continue;
            double angle  = 2 * Math.PI * i / notes.Count - Math.PI / 2;
            double spread = Math.Max(220, notes.Count * 45);
            _nodePositions[notes[i]] = (
                cx + spread * Math.Cos(angle) + (rng.NextDouble() - 0.5) * spread * 0.35,
                cy + spread * Math.Sin(angle) + (rng.NextDouble() - 0.5) * spread * 0.35);
        }

        // Force-directed pre-bake (150 iters)
        for (int iter = 0; iter < 150; iter++)
        {
            var forces = notes.ToDictionary(n => n, _ => (fx: 0.0, fy: 0.0));
            for (int i = 0; i < notes.Count; i++)
                for (int j = i + 1; j < notes.Count; j++)
                {
                    var (x1,y1) = _nodePositions[notes[i]];
                    var (x2,y2) = _nodePositions[notes[j]];
                    double dx = x1-x2, dy = y1-y2;
                    double dist = Math.Max(Math.Sqrt(dx*dx+dy*dy), 1);
                    double f = 6000.0 / (dist * dist);
                    forces[notes[i]] = (forces[notes[i]].fx + dx/dist*f, forces[notes[i]].fy + dy/dist*f);
                    forces[notes[j]] = (forces[notes[j]].fx - dx/dist*f, forces[notes[j]].fy - dy/dist*f);
                }
            foreach (var link in links)
            {
                var a = link.A;
                var b = link.B;
                var (x1,y1) = _nodePositions[a]; var (x2,y2) = _nodePositions[b];
                double dx = x2-x1, dy = y2-y1;
                double dist = Math.Max(Math.Sqrt(dx*dx+dy*dy), 1);
                double target = Math.Max(110, 280 - link.Score * 130);
                double f = (dist - target) / target * (0.12 + link.Score * 0.28);
                forces[a] = (forces[a].fx + dx/dist*f, forces[a].fy + dy/dist*f);
                forces[b] = (forces[b].fx - dx/dist*f, forces[b].fy - dy/dist*f);
            }
            double damp = Math.Max(0.04, 0.92 - iter * 0.006);
            foreach (var n in notes)
            {
                var (x,y)   = _nodePositions[n];
                var (fx,fy) = forces[n];
                _nodePositions[n] = (x + fx*damp, y + fy*damp);
            }
        }

        // Draw lines FIRST so they sit behind nodes
        // Draw ALL notes with at least a faint background line to every neighbour (Obsidian style)
        // plus brighter lines for actual references
        DrawGraphLines(notes, links);

        // Draw nodes
        foreach (var note in notes)
            DrawGraphNode(note, connCount[note]);

        // Snap render to current offset so no initial slide
        _renderX = _graphOffsetX; _renderY = _graphOffsetY; _renderScale = _graphScale;
        ApplyRenderTransform();
        _lastRenderTime = DateTime.UtcNow;

        // Hook into WPF render loop — fires every frame regardless of monitor Hz
        CompositionTarget.Rendering += GraphRenderFrame;
    }

    private List<GraphLink> BuildGraphLinks(List<SecureNote> notes)
    {
        var profiles = notes.ToDictionary(n => n, BuildGraphProfile);
        var candidates = new List<GraphLink>();

        for (var i = 0; i < notes.Count; i++)
        {
            for (var j = i + 1; j < notes.Count; j++)
            {
                var a = notes[i];
                var b = notes[j];
                var score = ScoreNoteRelevance(a, b, profiles[a], profiles[b], out var isExplicit);
                if (score >= 0.12 || isExplicit)
                    candidates.Add(new GraphLink(a, b, score, isExplicit));
            }
        }

        var topKeys = new HashSet<string>();
        foreach (var note in notes)
        {
            foreach (var link in candidates
                         .Where(l => ReferenceEquals(l.A, note) || ReferenceEquals(l.B, note))
                         .OrderByDescending(l => l.Score)
                         .Take(4))
            {
                topKeys.Add(GraphPairKey(link.A, link.B));
            }
        }

        return candidates
            .Where(l => l.Explicit || l.Score >= 0.34 || (l.Score >= 0.16 && topKeys.Contains(GraphPairKey(l.A, l.B))))
            .OrderByDescending(l => l.Score)
            .Take(Math.Max(12, notes.Count * 5))
            .ToList();
    }

    private sealed record GraphProfile(
        Dictionary<string, double> Terms,
        HashSet<string> TitleTerms,
        HashSet<string> Tags,
        HashSet<string> Domains,
        string SearchText);

    private static GraphProfile BuildGraphProfile(SecureNote note)
    {
        var terms = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var titleTerms = TokenizeGraphText(note.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var term in titleTerms) AddGraphTerm(terms, term, 2.7);

        foreach (var term in TokenizeGraphText(note.Body))
            AddGraphTerm(terms, term, 1.0);

        foreach (var term in TokenizeGraphText(note.Folder))
            AddGraphTerm(terms, term, 1.5);

        var tags = (note.Tags ?? new List<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
            foreach (var term in TokenizeGraphText(tag))
                AddGraphTerm(terms, term, 3.0);

        var searchText = $"{note.Title} {note.Folder} {string.Join(" ", note.Tags ?? new List<string>())} {note.Body}".ToLowerInvariant();
        return new GraphProfile(terms, titleTerms, tags, DetectGraphDomains(terms.Keys, searchText), searchText);
    }

    private static double ScoreNoteRelevance(
        SecureNote a,
        SecureNote b,
        GraphProfile pa,
        GraphProfile pb,
        out bool explicitLink)
    {
        explicitLink = ContainsGraphPhrase(pa.SearchText, b.Title) || ContainsGraphPhrase(pb.SearchText, a.Title);
        var score = explicitLink ? 0.52 : 0.0;

        var sharedTags = pa.Tags.Intersect(pb.Tags, StringComparer.OrdinalIgnoreCase).Count();
        if (sharedTags > 0)
            score += 0.22 + Math.Min(0.16, (sharedTags - 1) * 0.06);

        if (!string.IsNullOrWhiteSpace(a.Folder) &&
            string.Equals(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase))
            score += 0.12;

        var termSimilarity = WeightedGraphSimilarity(pa.Terms, pb.Terms);
        score += Math.Min(0.42, termSimilarity * 0.78);

        var titleOverlap = pa.TitleTerms.Intersect(pb.Terms.Keys, StringComparer.OrdinalIgnoreCase).Count()
                         + pb.TitleTerms.Intersect(pa.Terms.Keys, StringComparer.OrdinalIgnoreCase).Count();
        score += Math.Min(0.18, titleOverlap * 0.045);

        var sharedDomains = pa.Domains.Intersect(pb.Domains, StringComparer.OrdinalIgnoreCase).Count();
        score += Math.Min(0.24, sharedDomains * 0.14);

        return Math.Clamp(score, 0, 1);
    }

    private static double WeightedGraphSimilarity(Dictionary<string, double> a, Dictionary<string, double> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var dot = 0.0;
        foreach (var (term, weight) in a)
            if (b.TryGetValue(term, out var other))
                dot += weight * other;

        var normA = Math.Sqrt(a.Values.Sum(v => v * v));
        var normB = Math.Sqrt(b.Values.Sum(v => v * v));
        return normA <= 0 || normB <= 0 ? 0 : dot / (normA * normB);
    }

    private static IEnumerable<string> TokenizeGraphText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        foreach (Match match in Regex.Matches(text.ToLowerInvariant(), "[a-z0-9][a-z0-9+#.-]{1,}"))
        {
            var term = match.Value.Trim('.', '-', '_');
            if (term.Length < 2 || GraphStopWords.Contains(term)) continue;
            yield return term;
        }
    }

    private static void AddGraphTerm(Dictionary<string, double> terms, string term, double weight)
    {
        if (GraphStopWords.Contains(term)) return;
        terms[term] = terms.TryGetValue(term, out var existing) ? existing + weight : weight;
    }

    private static HashSet<string> DetectGraphDomains(IEnumerable<string> terms, string searchText)
    {
        var termSet = terms.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (domain, keywords) in GraphDomainKeywords)
        {
            if (keywords.Any(k => termSet.Contains(k) || (k.Length > 3 && searchText.Contains(k, StringComparison.OrdinalIgnoreCase))))
                domains.Add(domain);
        }
        return domains;
    }

    private static bool ContainsGraphPhrase(string haystack, string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase)) return false;
        phrase = phrase.Trim().ToLowerInvariant();
        return phrase.Length >= 3 && haystack.Contains(phrase, StringComparison.OrdinalIgnoreCase);
    }

    private static string GraphPairKey(SecureNote a, SecureNote b)
        => string.CompareOrdinal(a.Id, b.Id) <= 0 ? $"{a.Id}|{b.Id}" : $"{b.Id}|{a.Id}";

    private static readonly HashSet<string> GraphStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "that", "this", "into", "onto", "your", "you", "are", "was",
        "were", "has", "have", "had", "not", "but", "all", "any", "can", "will", "just", "about", "what",
        "when", "where", "why", "how", "note", "notes", "key", "keys", "code", "text", "todo", "list"
    };

    private static readonly Dictionary<string, string[]> GraphDomainKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ai"] = new[] { "ai", "artificial", "intelligence", "llm", "gpt", "openai", "claude", "anthropic", "codex", "model", "prompt", "api", "agent" },
        ["github"] = new[] { "github", "git", "repo", "repository", "branch", "commit", "pull", "request", "pr", "issue", "actions", "workflow" },
        ["security"] = new[] { "security", "cyber", "password", "vault", "secret", "token", "malware", "threat", "vulnerability", "recovery", "encrypt", "encrypted" },
        ["crypto"] = new[] { "crypto", "wallet", "seed", "blockchain", "coin", "bitcoin", "ethereum", "recovery", "phrase" },
        ["cloud"] = new[] { "cloud", "azure", "aws", "server", "service", "deployment", "hosting", "api" }
    };

    private void DrawGraphLines(List<SecureNote> notes, List<GraphLink> links)
    {
        var strongKeys = links.Select(l => GraphPairKey(l.A, l.B)).ToHashSet(StringComparer.Ordinal);
        var faintKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var a in notes)
        {
            if (!_nodePositions.ContainsKey(a)) continue;
            var (ax, ay) = _nodePositions[a];
            var nearest = notes
                .Where(b => b != a && _nodePositions.ContainsKey(b))
                .OrderBy(b =>
                {
                    var (bx, by) = _nodePositions[b];
                    return (bx - ax) * (bx - ax) + (by - ay) * (by - ay);
                })
                .Take(2);

            foreach (var b in nearest)
            {
                var key = GraphPairKey(a, b);
                if (strongKeys.Contains(key) || !faintKeys.Add(key)) continue;
                var (bx, by) = _nodePositions[b];
                var faint = new Line
                {
                    X1 = ax,
                    Y1 = ay,
                    X2 = bx,
                    Y2 = by,
                    Stroke = new SolidColorBrush(Color.FromArgb(38, GraphLineColor.R, GraphLineColor.G, GraphLineColor.B)),
                    StrokeThickness = 0.75,
                    Opacity = 0.72
                };
                Panel.SetZIndex(faint, -2);
                GraphInnerCanvas.Children.Add(faint);
            }
        }

        foreach (var link in links.OrderBy(l => l.Score))
        {
            if (!_nodePositions.ContainsKey(link.A) || !_nodePositions.ContainsKey(link.B)) continue;
            var (x1,y1) = _nodePositions[link.A]; var (x2,y2) = _nodePositions[link.B];
            var alpha = (byte)Math.Clamp(60 + link.Score * 170, 55, 220);
            var stroke = Color.FromArgb(alpha, GraphLineColor.R, GraphLineColor.G, GraphLineColor.B);
            var line = new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Stroke = new SolidColorBrush(stroke),
                StrokeThickness = Math.Clamp(0.75 + link.Score * 2.7, 0.8, 3.2),
                Opacity = link.Explicit ? 0.95 : 0.82,
                ToolTip = $"Relevance {link.Score:P0}"
            };
            Panel.SetZIndex(line, -1);
            GraphInnerCanvas.Children.Add(line);
        }
    }

    // WPF render-loop hook — framerate-independent via elapsed time
    private void GraphRenderFrame(object? sender, EventArgs e)
    {
        var now   = DateTime.UtcNow;
        double dt = Math.Min((now - _lastRenderTime).TotalSeconds, 0.05); // cap at 50ms
        _lastRenderTime = now;

        double speedFactor = 1.0 - Math.Pow(0.005, dt); // ~12% per 16ms regardless of Hz

        bool changed = false;

        double dxP = _graphOffsetX - _renderX;
        double dyP = _graphOffsetY - _renderY;
        double dsP = _graphScale   - _renderScale;

        if (Math.Abs(dxP) > 0.3) { _renderX += dxP * speedFactor; changed = true; } else _renderX = _graphOffsetX;
        if (Math.Abs(dyP) > 0.3) { _renderY += dyP * speedFactor; changed = true; } else _renderY = _graphOffsetY;
        if (Math.Abs(dsP) > 0.0008) { _renderScale += dsP * speedFactor; changed = true; } else _renderScale = _graphScale;

        if (changed) ApplyRenderTransform();

        // Node inertia
        if (_draggingNode == null && _draggedNote != null &&
            (Math.Abs(_dragVx) > 0.2 || Math.Abs(_dragVy) > 0.2))
        {
            double friction = Math.Pow(0.85, dt * 60);
            _dragVx *= friction; _dragVy *= friction;
            if (_nodePositions.TryGetValue(_draggedNote, out var cur))
            {
                var nx = cur.x + _dragVx; var ny = cur.y + _dragVy;
                _nodePositions[_draggedNote] = (nx, ny);
                MoveNodeVisual(_draggedNote, nx, ny);
                RedrawLines();
            }
            if (Math.Abs(_dragVx) < 0.2 && Math.Abs(_dragVy) < 0.2) _draggedNote = null;
        }
    }

    private void ApplyRenderTransform()
    {
        if (GraphInnerCanvas == null) return;
        var tg = new TransformGroup();
        tg.Children.Add(new ScaleTransform(_renderScale, _renderScale));
        tg.Children.Add(new TranslateTransform(_renderX, _renderY));
        GraphInnerCanvas.RenderTransform = tg;
    }

    private void ApplyGraphTransform()
    {
        _renderX = _graphOffsetX; _renderY = _graphOffsetY; _renderScale = _graphScale;
        ApplyRenderTransform();
    }

    private void MoveNodeVisual(SecureNote note, double nx, double ny)
    {
        if (!_nodeVisuals.TryGetValue(note, out var v)) return;
        Canvas.SetLeft(v.el,  nx - v.r); Canvas.SetTop(v.el,  ny - v.r);
        Canvas.SetLeft(v.lbl, nx - 44); Canvas.SetTop(v.lbl, ny + v.r + 5);
    }

    private void DrawGraphNode(SecureNote note, int connections)
    {
        if (!_nodePositions.TryGetValue(note, out var pos)) return;
        var (nx, ny) = pos;
        double r = Math.Min(8 + connections * 4, 24);

        Color fill = connections > 4 ? GraphHubColor : GraphNodeColor;

        var el = new Ellipse
        {
            Width  = r * 2, Height = r * 2,
            Fill   = new SolidColorBrush(fill),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = fill, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.6 },
            Cursor = Cursors.Hand, ToolTip = note.Title
        };
        Panel.SetZIndex(el, 1);
        Canvas.SetLeft(el, nx - r); Canvas.SetTop(el, ny - r);

        var lbl = new TextBlock
        {
            Text = note.Title,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0,0xE0,0xE0)),
            FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = Colors.Black, BlurRadius = 3, ShadowDepth = 1, Opacity = 0.9 }
        };
        Panel.SetZIndex(lbl, 2);
        Canvas.SetLeft(lbl, nx - 44); Canvas.SetTop(lbl, ny + r + 5);

        _nodeVisuals[note] = (el, lbl, r, fill);

        el.MouseEnter += (s, _) =>
        {
            ((Ellipse)s).Fill = new SolidColorBrush(Color.FromRgb(0xFF,0xFF,0x00));
            ((Ellipse)s).RenderTransformOrigin = new Point(0.5, 0.5);
            ((Ellipse)s).RenderTransform = new ScaleTransform(1.45, 1.45);
        };
        el.MouseLeave += (s, _) =>
        {
            ((Ellipse)s).Fill = new SolidColorBrush(fill);
            ((Ellipse)s).RenderTransform = Transform.Identity;
        };
        el.MouseMove += Node_MouseMove;
        el.MouseLeftButtonUp += Node_MouseUp;
        el.MouseLeftButtonDown += (s, ev) =>
        {
            if (ev.ClickCount == 2)
            {
                if (_nodeMap.TryGetValue((Ellipse)s, out var n))
                { ShowNotes(); OpenNote(n); SelectNoteInTree(n); }
                ev.Handled = true; return;
            }
            _draggingNode = (Ellipse)s;
            _draggedNote  = _nodeMap.TryGetValue(_draggingNode, out var dn) ? dn : null;
            var p = ev.GetPosition(GraphInnerCanvas);
            _nodeDragOffset = new Point(
                p.X - (Canvas.GetLeft(_draggingNode) + _draggingNode.Width  / 2),
                p.Y - (Canvas.GetTop(_draggingNode)  + _draggingNode.Height / 2));
            _dragVx = 0; _dragVy = 0;
            _draggingNode.CaptureMouse();
            ev.Handled = true;
        };

        _nodeMap[el] = note;
        GraphInnerCanvas.Children.Add(el);
        GraphInnerCanvas.Children.Add(lbl);
    }

    private void Node_MouseDown(object sender, MouseButtonEventArgs e) { }

    private void Node_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingNode == null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(GraphInnerCanvas);
        double nx = pos.X - _nodeDragOffset.X;
        double ny = pos.Y - _nodeDragOffset.Y;

        if (_draggedNote != null && _nodePositions.TryGetValue(_draggedNote, out var prev))
        { _dragVx = nx - prev.x; _dragVy = ny - prev.y; }

        if (_draggedNote != null)
        { _nodePositions[_draggedNote] = (nx, ny); MoveNodeVisual(_draggedNote, nx, ny); RedrawLines(); }
    }

    private void Node_MouseUp(object sender, MouseButtonEventArgs e)
    { _draggingNode?.ReleaseMouseCapture(); _draggingNode = null; }

    private void RedrawLines()
    {
        foreach (var l in GraphInnerCanvas.Children.OfType<Line>().ToList())
            GraphInnerCanvas.Children.Remove(l);
        var notes = _vault.Notes.ToList();
        DrawGraphLines(notes, BuildGraphLinks(notes));
    }

    private void GraphCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    { if (e.OriginalSource is Canvas) { _isPanning = true; _panStart = e.GetPosition(GraphCanvas); GraphCanvas.CaptureMouse(); } }

    private void GraphCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isPanning && e.LeftButton == MouseButtonState.Pressed)
        {
            var p = e.GetPosition(GraphCanvas);
            _graphOffsetX += p.X - _panStart.X;
            _graphOffsetY += p.Y - _panStart.Y;
            _panStart = p;
        }
    }

    private void GraphCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    { _isPanning = false; GraphCanvas.ReleaseMouseCapture(); }

    private void GraphCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var pos = e.GetPosition(GraphCanvas);
        double old = _graphScale;
        _graphScale = Math.Clamp(_graphScale * (1 + e.Delta / 1000.0), 0.08, 8.0);
        double ratio = _graphScale / old;
        _graphOffsetX = pos.X - ratio * (pos.X - _graphOffsetX);
        _graphOffsetY = pos.Y - ratio * (pos.Y - _graphOffsetY);
    }

    private void GraphReset_Click(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering -= GraphRenderFrame;
        _nodePositions.Clear();
        _draggedNote = null; _dragVx = 0; _dragVy = 0;
        _graphOffsetX = 0; _graphOffsetY = 0; _graphScale = 1.0;
        _renderX = 0; _renderY = 0; _renderScale = 1.0;
        ApplyRenderTransform();
        BuildGraph();
    }

    // Called from settings when colors change
    public void RefreshGraphColors()
    {
        _nodePositions.Clear();
        if (GraphView.Visibility == Visibility.Visible)
            BuildGraph();
    }

    // ---- KEYBOARD SHORTCUTS ----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control) { OpenCommandPalette(); e.Handled = true; }
        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control) { ShowNotes(); CreateNewNote(); e.Handled = true; }
        if (e.Key == Key.P && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { OpenCommandPalette(); e.Handled = true; }
        if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ShowHome();
            GlobalSearchBox.Focus();
            GlobalSearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OpenCommandPalette()
    {
        var commands = new System.Collections.Generic.List<CommandItem>
        {
            new() { Icon = "🏠", Label = "Go to Home",         Shortcut = "",        Execute = ShowHome },
            new() { Icon = "🔐", Label = "Go to Passwords",    Shortcut = "",        Execute = ShowPasswordManager },
            new() { Icon = "📝", Label = "Go to Notes",         Shortcut = "",        Execute = ShowNotes },
            new() { Icon = "🕸",  Label = "Go to Graph",        Shortcut = "",        Execute = () => ShowGraph() },
            new() { Icon = "🛡",  Label = "Security Insights",  Shortcut = "",        Execute = () => { SetView(SecurityView); SetActiveTab(BtnSecurity); RunSecurityCheck(); } },
            new() { Icon = "➕", Label = "New Note",            Shortcut = "Ctrl+N",  Execute = () => { ShowNotes(); CreateNewNote(); } },
            new() { Icon = "➕", Label = "Add Password",        Shortcut = "",        Execute = () => { ShowPasswordManager(); Add_Click(this, new RoutedEventArgs()); } },
            new() { Icon = "🔒", Label = "Lock Vault",          Shortcut = "",        Execute = Lock },
            new() { Icon = "⚙",  Label = "Settings",            Shortcut = "",        Execute = ShowSettings },
            new() { Icon = "🗑",  Label = "View Trash",          Shortcut = "",        Execute = () => { SetView(TrashView); SetActiveTab(BtnTrash); RefreshTrash(); } },
        };
        // Add notes as commands
        foreach (var note in _vault.Notes.Take(20))
        {
            var n = note;
            commands.Add(new CommandItem { Icon = "📄", Label = note.Title, Sub = "Note", Execute = () => { ShowNotes(); OpenNote(n); SelectNoteInTree(n); } });
        }
        var palette = new CommandPalette(commands) { Owner = this };
        palette.ShowDialog();
    }

    // ---- GLOBAL SEARCH ----

    public class SearchResult
    {
        public string Icon { get; set; } = "";
        public string Title { get; set; } = "";
        public string Sub { get; set; } = "";
        public System.Action? Navigate { get; set; }
    }

    private void GlobalSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = GlobalSearchBox.Text.Trim();
        if (string.IsNullOrEmpty(q)) { GlobalSearchResults.Visibility = Visibility.Collapsed; return; }

        var results = new System.Collections.Generic.List<SearchResult>();
        foreach (var note in FuzzySearch.Search(_vault.Notes, q, n => n.Title + " " + n.Body, 10))
        {
            var n = note;
            results.Add(new SearchResult { Icon = "📝", Title = note.Title, Sub = "Note",
                Navigate = () => { ShowNotes(); OpenNote(n); SelectNoteInTree(n); } });
        }
        foreach (var entry in FuzzySearch.Search(_vault.Entries, q, e2 => e2.Title + " " + e2.Username, 10))
            results.Add(new SearchResult { Icon = "🔐", Title = entry.Title, Sub = entry.Username });

        GlobalResultsList.ItemsSource = results;
        GlobalSearchResults.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void GlobalResult_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GlobalResultsList.SelectedItem is SearchResult r) { r.Navigate?.Invoke(); GlobalSearchBox.Text = ""; GlobalSearchResults.Visibility = Visibility.Collapsed; }
    }

    // ---- VERSION HISTORY ----

    private void ShowHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNote == null) { new BastionDialog("No note open", "Open a note first.", false).ShowDialog(); return; }
        if (_currentNote.History.Count == 0) { new BastionDialog("No history", "No snapshots yet. History is saved automatically as you edit.", false).ShowDialog(); return; }
        var dlg = new VersionHistoryDialog(_currentNote) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.RestoredBody != null)
        {
            _suppressNoteChange = true;
            NoteBodyEditor.Text = dlg.RestoredBody;
            if (dlg.RestoredTitle != null) NoteTitleEditor.Text = dlg.RestoredTitle;
            _suppressNoteChange = false;
            SaveCurrentNote();
        }
    }

    // ---- PIN NOTE ----

    private void TogglePinNote_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNote == null) return;
        _currentNote.IsPinned = !_currentNote.IsPinned;
        UpdatePinButton();
        VaultStore.Save(_vault, _password);
        RefreshNotesTree();
    }

    private void UpdatePinButton()
    {
        if (_currentNote == null || BtnPinNote == null) return;
        BtnPinNote.Content = _currentNote.IsPinned ? "📌 Pinned" : "📌 Pin";
        BtnPinNote.Opacity = _currentNote.IsPinned ? 1.0 : 0.5;
    }

    // ---- BACKLINKS ----

    private void UpdateBacklinks()
    {
        if (_currentNote == null || BacklinksList == null) return;
        var wikiLink = $"[[{_currentNote.Title}]]";
        var backlinks = _vault.Notes
            .Where(n => n != _currentNote &&
                ((n.Body ?? "").Contains(_currentNote.Title, StringComparison.OrdinalIgnoreCase) ||
                 (n.Body ?? "").Contains(wikiLink, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        BacklinksList.ItemsSource = backlinks;
    }

    private void Backlink_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BacklinksList.SelectedItem is SecureNote n) { OpenNote(n); SelectNoteInTree(n); }
    }

    // ---- SECURITY INSIGHTS ----

    private void RunSecurityCheck_Click(object sender, RoutedEventArgs e) => RunSecurityCheck();

    private void RunSecurityCheck()
    {
        var weak   = SecurityInsights.FindWeak(_vault.Entries);
        var reused = SecurityInsights.FindReused(_vault.Entries);
        var dupes  = SecurityInsights.FindDuplicates(_vault.Entries);
        WeakCount.Text   = weak.Count.ToString();
        ReusedCount.Text = reused.Count.ToString();
        DupeCount.Text   = dupes.Count.ToString();
        ShowWeakList(weak);
    }

    private void ShowWeak_Click(object sender, RoutedEventArgs e)  { SecurityTitle.Text = "WEAK PASSWORDS";      ShowWeakList(SecurityInsights.FindWeak(_vault.Entries)); }
    private void ShowReused_Click(object sender, RoutedEventArgs e){ SecurityTitle.Text = "REUSED PASSWORDS";    ShowWeakList(SecurityInsights.FindReused(_vault.Entries)); }
    private void ShowDupes_Click(object sender, RoutedEventArgs e) { SecurityTitle.Text = "DUPLICATE ENTRIES";   ShowWeakList(SecurityInsights.FindDuplicates(_vault.Entries)); }

    private async void RunBreachCheck_Click(object sender, RoutedEventArgs e)
    {
        SecurityTitle.Text = "BREACH CHECK";
        SecurityList.ItemsSource = new[] { new { Title = "Checking...", Username = "", StrengthLabel = "SHA-1 prefix lookup", Details = "Using k-anonymity; only the first 5 SHA-1 characters are sent" } };
        var results = new List<object>();
        foreach (var entry in _vault.Entries.Where(e => !string.IsNullOrEmpty(e.Password)))
        {
            var count = await SecurityInsights.CheckBreachAsync(entry.Password);
            if (count > 0)
                results.Add(new { entry.Title, entry.Username, StrengthLabel = $"Seen {count:N0} times", Details = "Change this password anywhere it is used" });
            else if (count == -1)
                results.Add(new { entry.Title, entry.Username, StrengthLabel = "Check failed", Details = "Network or service error" });
        }
        SecurityList.ItemsSource = results.Count > 0
            ? results
            : new[] { new { Title = "No breached passwords found", Username = "", StrengthLabel = "", Details = "" } };
    }

    private void ShowWeakList(System.Collections.Generic.List<VaultEntry> entries)
    {
        SecurityList.ItemsSource = entries.Select(e2 => new
        {
            e2.Title, e2.Username,
            StrengthLabel = SecurityInsights.AnalyzePassword(e2.Password).Display,
            Details = SecurityInsights.AnalyzePassword(e2.Password).Summary
        }).ToList();
    }

    // ---- TRASH ----

    private void RefreshTrash()
    {
        TrashPasswordsList.ItemsSource = _vault.Trash.ToList();
        TrashNotesList.ItemsSource = _vault.NoteTrash.ToList();
    }

    private void RestorePassword_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is VaultEntry v)
        {
            _vault.Trash.Remove(v); _vault.Entries.Add(v);
            VaultStore.Save(_vault, _password); RefreshTrash(); UpdateHomeStats(); VaultList.Items.Refresh();
        }
    }

    private void RestoreNote_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is SecureNote n)
        {
            _vault.NoteTrash.Remove(n); _vault.Notes.Add(n);
            VaultStore.Save(_vault, _password); RefreshTrash(); UpdateHomeStats(); RefreshNotesTree();
        }
    }

    private void EmptyTrash_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new BastionDialog("Empty Trash?", "All items in trash will be permanently deleted. This cannot be undone.", true);
        if (dlg.ShowDialog() == true)
        {
            _vault.Trash.Clear(); _vault.NoteTrash.Clear();
            VaultStore.Save(_vault, _password); RefreshTrash();
        }
    }

    // ---- SETTINGS EXTRAS ----
    private void OpenGraphColors_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new GraphColorDialog(GraphNodeColor, GraphHubColor, GraphLineColor) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            GraphNodeColor = dlg.NodeColor;
            GraphHubColor  = dlg.HubColor;
            GraphLineColor = dlg.LineColor;
            SaveGraphColors();
            RefreshGraphColors();
        }
    }

    private static string GraphColorPrefsPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bastion", "graph_colors.json");

    private void SaveGraphColors()
    {
        try
        {
            _vault.Settings.GraphNodeColor = ColorToHex(GraphNodeColor);
            _vault.Settings.GraphHubColor = ColorToHex(GraphHubColor);
            _vault.Settings.GraphLineColor = ColorToHex(GraphLineColor);
            VaultStore.Save(_vault, _password);

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(GraphColorPrefsPath)!);
            var obj = new { node = ColorToHex(GraphNodeColor), hub = ColorToHex(GraphHubColor), line = ColorToHex(GraphLineColor) };
            File.WriteAllText(GraphColorPrefsPath, System.Text.Json.JsonSerializer.Serialize(obj));
        }
        catch { }
    }

    private void LoadGraphColors()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_vault?.Settings?.GraphNodeColor))
            {
                GraphNodeColor = HexToColor(_vault.Settings.GraphNodeColor);
                GraphHubColor = HexToColor(_vault.Settings.GraphHubColor);
                GraphLineColor = HexToColor(_vault.Settings.GraphLineColor);
                return;
            }

            if (!File.Exists(GraphColorPrefsPath)) return;
            var json = File.ReadAllText(GraphColorPrefsPath);
            var doc  = System.Text.Json.JsonDocument.Parse(json);
            GraphNodeColor = HexToColor(doc.RootElement.GetProperty("node").GetString() ?? "");
            GraphHubColor  = HexToColor(doc.RootElement.GetProperty("hub").GetString() ?? "");
            GraphLineColor = HexToColor(doc.RootElement.GetProperty("line").GetString() ?? "");
        }
        catch { }
    }

    private static string ColorToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    private static Color HexToColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Color.FromRgb(0xEE, 0x00, 0xAA); }
    }

    private void ShowAutofillToken_Click(object sender, RoutedEventArgs e)
    {
        var token = _localApi?.Token ?? "API not running";
        new BastionDialog("Browser Extension",
            $"The extension connects automatically when Bastion is open.\n\nToken file: {BastionLocalApi.TokenFilePath}\n\nToken rotates when Bastion starts and is removed when the vault is locked.", false).ShowDialog();
    }

    private void ShowEncryptedShareInfo_Click(object sender, RoutedEventArgs e)
    {
        new BastionDialog("Encrypted shares",
            "Export encrypted share creates a .bastion-share file containing a password-protected copy of passwords, secure notes, tags, and note attachments.\n\nHow to export:\n1. Click Export encrypted share.\n2. Choose where to save the .bastion-share file.\n3. Send the file only through a channel you trust.\n\nHow to import:\n1. Click Import encrypted share.\n2. Choose the .bastion-share file.\n3. Enter the password that was used when it was exported.\n4. Bastion decrypts it and merges non-duplicate passwords and notes into this vault.\n\nExpired shares show a warning before import. Shares are for moving or sharing selected vault data; encrypted backups are better for full vault recovery.",
            false).ShowDialog();
    }

    private void CreateVaultBackup_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentNote();
        VaultStore.Save(_vault, _password);

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Bastion Backup (*.bastion-backup)|*.bastion-backup|Vault Data (*.dat)|*.dat",
            FileName = $"bastion-backup-{DateTime.Now:yyyyMMdd-HHmm}.bastion-backup"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            File.Copy(VaultStore.VaultPath, dlg.FileName, overwrite: true);
            new BastionDialog("Backup created",
                $"Encrypted backup saved to:\n{dlg.FileName}\n\nThis backup still requires your current master password.",
                false) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            new BastionDialog("Backup failed", ex.Message, false) { Owner = this }.ShowDialog();
        }
    }

    private void RestoreVaultBackup_Click(object sender, RoutedEventArgs e)
    {
        var openDlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Bastion Backup (*.bastion-backup)|*.bastion-backup|Vault Data (*.dat)|*.dat"
        };
        if (openDlg.ShowDialog() != true) return;

        var confirm = new BastionDialog("Restore backup?",
            "This will replace the currently open vault with the selected encrypted backup. Make sure you know the master password used for that backup.",
            true) { Owner = this };
        if (confirm.ShowDialog() != true) return;

        try
        {
            var restored = VaultStore.LoadFromFile(openDlg.FileName, _password);
            File.Copy(openDlg.FileName, VaultStore.VaultPath, overwrite: true);
            _vault = restored;
            NormalizeVault();
            StartLocalApi();
            VaultList.ItemsSource = _vault.Entries;
            RefreshPasswordList();
            RefreshNotesTree();
            RefreshTagPanel();
            RefreshTrash();
            UpdateHomeStats();
            UpdateSettingsControls();
            if (_vault.Notes.Count > 0) OpenNote(_vault.Notes.OrderByDescending(n => n.UpdatedAt).First());
            else ClearCurrentNoteUi();

            new BastionDialog("Backup restored",
                "The encrypted backup was restored and the vault was reloaded.",
                false) { Owner = this }.ShowDialog();
        }
        catch (CryptographicException)
        {
            new BastionDialog("Restore failed",
                "The backup could not be decrypted with the current master password.",
                false) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            new BastionDialog("Restore failed", ex.Message, false) { Owner = this }.ShowDialog();
        }
    }

    private void UpdateSettingsControls()
    {
        _isUpdatingSettingsControls = true;
        try
        {
            if (AutofillEnabledCheck != null) AutofillEnabledCheck.IsChecked = _vault.Settings.AutofillEnabled;
            if (RunInTrayCheck != null) RunInTrayCheck.IsChecked = _vault.Settings.RunInTray;
            if (StartOnBootCheck != null) StartOnBootCheck.IsChecked = _vault.Settings.StartOnBoot;
            if (StartHiddenToTrayCheck != null)
            {
                StartHiddenToTrayCheck.IsChecked = _vault.Settings.StartHiddenToTray;
                StartHiddenToTrayCheck.IsEnabled = _vault.Settings.StartOnBoot;
            }
            if (DarkThemeCheck != null) DarkThemeCheck.IsChecked = _vault.Settings.DarkTheme;
            if (AccentColorBox != null) AccentColorBox.Text = _vault.Settings.AccentColor;
            if (GraphNodeColorBox != null) GraphNodeColorBox.Text = ColorToHex(GraphNodeColor);
            if (GraphHubColorBox != null) GraphHubColorBox.Text = ColorToHex(GraphHubColor);
            if (GraphLineColorBox != null) GraphLineColorBox.Text = ColorToHex(GraphLineColor);
            EnsureSettingsColorMaps();
            UpdateColorPreviews();
            if (LockTimeoutCombo != null)
                SelectLockTimeoutItem(_vault.Settings.LockMinutes);
            if (ClipboardTimeoutCombo != null)
                SelectClipboardTimeoutItem(_vault.Settings.ClipboardTimeoutSeconds);
        }
        finally
        {
            _isUpdatingSettingsControls = false;
        }
    }

    private void SelectLockTimeoutItem(int minutes)
    {
        foreach (var item in LockTimeoutCombo.Items.OfType<ComboBoxItem>())
        {
            if (int.TryParse(item.Tag?.ToString(), out var tagMinutes) && tagMinutes == minutes)
            {
                LockTimeoutCombo.SelectedItem = item;
                return;
            }
        }

        LockTimeoutCombo.SelectedIndex = 2;
    }

    private void SelectClipboardTimeoutItem(int seconds)
    {
        foreach (var item in ClipboardTimeoutCombo.Items.OfType<ComboBoxItem>())
        {
            if (int.TryParse(item.Tag?.ToString(), out var tagSeconds) && tagSeconds == seconds)
            {
                ClipboardTimeoutCombo.SelectedItem = item;
                return;
            }
        }

        ClipboardTimeoutCombo.SelectedIndex = 2;
    }

    private void ClipboardTimeout_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_vault?.Settings == null || ClipboardTimeoutCombo == null || _isUpdatingSettingsControls) return;
        if (ClipboardTimeoutCombo.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out var seconds))
        {
            _vault.Settings.ClipboardTimeoutSeconds = Math.Clamp(seconds, 0, 300);
            VaultStore.Save(_vault, _password);
        }
    }

    private void AutofillEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_vault?.Settings == null || AutofillEnabledCheck == null || _isUpdatingSettingsControls) return;
        _vault.Settings.AutofillEnabled = AutofillEnabledCheck.IsChecked == true;
        VaultStore.Save(_vault, _password);
    }

    private void TrayStartup_Changed(object sender, RoutedEventArgs e)
    {
        if (_vault?.Settings == null || _isUpdatingSettingsControls) return;

        _vault.Settings.RunInTray = RunInTrayCheck?.IsChecked == true;
        _vault.Settings.StartOnBoot = StartOnBootCheck?.IsChecked == true;
        _vault.Settings.StartHiddenToTray = _vault.Settings.StartOnBoot && StartHiddenToTrayCheck?.IsChecked == true;

        if (_vault.Settings.StartHiddenToTray)
            _vault.Settings.RunInTray = true;
        if (!_vault.Settings.StartOnBoot)
            _vault.Settings.StartHiddenToTray = false;

        ApplyStartupRegistration();
        VaultStore.Save(_vault, _password);
        UpdateSettingsControls();

        if (_vault.Settings.RunInTray)
            EnsureTrayIcon();
        else if (IsVisible)
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
        }
    }

    private void ApplyStartupRegistration()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;

            const string valueName = "Bastion";
            if (_vault.Settings.StartOnBoot)
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName
                              ?? System.Windows.Forms.Application.ExecutablePath;
                var args = _vault.Settings.StartHiddenToTray ? " --start-hidden" : "";
                key.SetValue(valueName, $"\"{exePath}\"{args}");
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            new BastionDialog("Startup setting failed",
                $"Bastion could not update the Windows startup setting:\n{ex.Message}",
                false) { Owner = this }.ShowDialog();
        }
    }

    private void Theme_Changed(object sender, RoutedEventArgs e)
    {
        if (_vault?.Settings == null || DarkThemeCheck == null || _isUpdatingSettingsControls) return;
        _vault.Settings.DarkTheme = DarkThemeCheck.IsChecked == true;
        ApplyTheme();
        VaultStore.Save(_vault, _password);
    }

    private void AccentColor_Changed(object sender, TextChangedEventArgs e)
    {
        if (_vault?.Settings == null || AccentColorBox == null || _isUpdatingSettingsControls || _isUpdatingColorText || string.IsNullOrWhiteSpace(AccentColorBox.Text)) return;
        if (!TryParseHexColor(AccentColorBox.Text.Trim(), out var color)) return;
        _vault.Settings.AccentColor = ColorToHex(color);
        UpdateColorPreviews();
        ApplyTheme();
        VaultStore.Save(_vault, _password);
    }

    private void GraphColorText_Changed(object sender, TextChangedEventArgs e)
    {
        if (_vault?.Settings == null || _isUpdatingSettingsControls || _isUpdatingColorText) return;
        if (sender == GraphNodeColorBox && TryParseHexColor(GraphNodeColorBox.Text, out var node))
            GraphNodeColor = node;
        else if (sender == GraphHubColorBox && TryParseHexColor(GraphHubColorBox.Text, out var hub))
            GraphHubColor = hub;
        else if (sender == GraphLineColorBox && TryParseHexColor(GraphLineColorBox.Text, out var line))
            GraphLineColor = line;
        else
            return;

        SaveGraphColors();
        UpdateColorPreviews();
        RefreshGraphColors();
    }

    private void EnsureSettingsColorMaps()
    {
        _colorMapSource ??= CreateColorMapBitmap(360, 120);
        if (AccentColorMap != null) AccentColorMap.Source = _colorMapSource;
        if (GraphNodeColorMap != null) GraphNodeColorMap.Source = _colorMapSource;
        if (GraphHubColorMap != null) GraphHubColorMap.Source = _colorMapSource;
        if (GraphLineColorMap != null) GraphLineColorMap.Source = _colorMapSource;
    }

    private void ColorMap_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Image image) return;
        image.CaptureMouse();
        ApplyColorFromMap(image, e.GetPosition(image));
        e.Handled = true;
    }

    private void ColorMap_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Image image || e.LeftButton != MouseButtonState.Pressed) return;
        ApplyColorFromMap(image, e.GetPosition(image));
        e.Handled = true;
    }

    private void ColorMap_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Image image)
            image.ReleaseMouseCapture();
    }

    private void ApplyColorFromMap(Image image, Point point)
    {
        var color = ColorFromMapPoint(point, image.ActualWidth, image.ActualHeight);
        switch (image.Tag?.ToString())
        {
            case "Accent":
                SetAccentColor(color);
                break;
            case "GraphNode":
                GraphNodeColor = color;
                SetColorBox(GraphNodeColorBox, color);
                SaveGraphColors();
                RefreshGraphColors();
                break;
            case "GraphHub":
                GraphHubColor = color;
                SetColorBox(GraphHubColorBox, color);
                SaveGraphColors();
                RefreshGraphColors();
                break;
            case "GraphLine":
                GraphLineColor = color;
                SetColorBox(GraphLineColorBox, color);
                SaveGraphColors();
                RefreshGraphColors();
                break;
        }

        UpdateColorPreviews();
    }

    private void SetAccentColor(Color color)
    {
        if (_vault?.Settings == null) return;
        _vault.Settings.AccentColor = ColorToHex(color);
        SetColorBox(AccentColorBox, color);
        ApplyTheme();
        VaultStore.Save(_vault, _password);
    }

    private void SetColorBox(TextBox? textBox, Color color)
    {
        if (textBox == null) return;
        _isUpdatingColorText = true;
        textBox.Text = ColorToHex(color);
        _isUpdatingColorText = false;
    }

    private void UpdateColorPreviews()
    {
        SetColorPreview(AccentColorPreview, GetAccentColor());
        SetColorPreview(GraphNodeColorPreview, GraphNodeColor);
        SetColorPreview(GraphHubColorPreview, GraphHubColor);
        SetColorPreview(GraphLineColorPreview, GraphLineColor);
    }

    private static void SetColorPreview(Border? preview, Color color)
    {
        if (preview == null) return;
        preview.Background = new SolidColorBrush(color);
    }

    private static BitmapSource CreateColorMapBitmap(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var yMix = height <= 1 ? 0.5 : y / (double)(height - 1);
            for (var x = 0; x < width; x++)
            {
                var hue = width <= 1 ? 0 : x * 360.0 / (width - 1);
                var color = ColorFromHueMap(hue, yMix);
                var index = (y * width + x) * 4;
                pixels[index] = color.B;
                pixels[index + 1] = color.G;
                pixels[index + 2] = color.R;
                pixels[index + 3] = 255;
            }
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        source.Freeze();
        return source;
    }

    private static Color ColorFromMapPoint(Point point, double width, double height)
    {
        var safeWidth = Math.Max(width, 1);
        var safeHeight = Math.Max(height, 1);
        var x = Math.Clamp(point.X, 0, safeWidth - 1);
        var y = Math.Clamp(point.Y, 0, safeHeight - 1);
        var hue = x * 360.0 / Math.Max(safeWidth - 1, 1);
        var yMix = y / Math.Max(safeHeight - 1, 1);
        return ColorFromHueMap(hue, yMix);
    }

    private static Color ColorFromHueMap(double hue, double yMix)
    {
        var saturated = ColorFromHsv(hue, 1.0, 1.0);
        return yMix < 0.5
            ? BlendColor(Colors.White, saturated, yMix * 2.0)
            : BlendColor(saturated, Colors.Black, (yMix - 0.5) * 2.0);
    }

    private static Color ColorFromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs((hue / 60.0) % 2 - 1));
        var m = value - chroma;
        (double r, double g, double b) = hue switch
        {
            < 60 => (chroma, x, 0.0),
            < 120 => (x, chroma, 0.0),
            < 180 => (0.0, chroma, x),
            < 240 => (0.0, x, chroma),
            < 300 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x)
        };
        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static Color BlendColor(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));
    }

    private Color GetAccentColor()
        => TryParseHexColor(_vault?.Settings?.AccentColor ?? "", out var color) ? color : Color.FromRgb(0x7C, 0x3A, 0xED);

    private static bool TryParseHexColor(string? hex, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        hex = hex.Trim();
        if (!hex.StartsWith("#", StringComparison.Ordinal)) hex = "#" + hex;
        if (hex.Length != 7 && hex.Length != 9) return false;

        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex);
            color.A = 255;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyTheme()
    {
        if (_vault?.Settings == null) return;
        _activeNavButton = GetNavButtonForCurrentView() ?? _activeNavButton ?? BtnHome;
        var isDark = _vault.Settings.DarkTheme;
        var bg = isDark ? "#161616" : "#F5F7FA";
        var panel = isDark ? "#141414" : "#FFFFFF";
        var fg = isDark ? "#E8E8E8" : "#1F2937";
        var border = isDark ? "#2A2A2A" : "#D8DEE8";
        var bgBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
        var panelBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(panel));
        var fgBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg));
        var borderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(border));

        CaptureThemeSnapshots(this);
        ApplyThemeRecursive(this, bgBrush, panelBrush, fgBrush, borderBrush, isDark);
        ApplyExplicitThemeControls();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyThemeRecursive(this, bgBrush, panelBrush, fgBrush, borderBrush, isDark);
            ApplyExplicitThemeControls();
        }), DispatcherPriority.Loaded);
    }

    private Button? GetNavButtonForCurrentView()
    {
        if (HomeView?.Visibility == Visibility.Visible) return BtnHome;
        if (PasswordManagerView?.Visibility == Visibility.Visible) return BtnPasswordManager;
        if (NotesView?.Visibility == Visibility.Visible) return BtnNotes;
        if (GraphView?.Visibility == Visibility.Visible) return BtnGraph;
        if (SecurityView?.Visibility == Visibility.Visible) return BtnSecurity;
        if (TrashView?.Visibility == Visibility.Visible) return BtnTrash;
        if (SettingsView?.Visibility == Visibility.Visible) return BtnSettings;
        return null;
    }

    private void ApplyExplicitThemeControls()
    {
        EnsureSettingsColorMaps();
        UpdateColorPreviews();
        ApplyNavigationTheme();
        ApplyCheckboxTheme(AutofillEnabledCheck);
        ApplyCheckboxTheme(RunInTrayCheck);
        ApplyCheckboxTheme(StartOnBootCheck);
        ApplyCheckboxTheme(StartHiddenToTrayCheck);
        ApplyCheckboxTheme(DarkThemeCheck);
        ApplyPasswordManagerTheme();
    }

    private void ApplyNavigationTheme()
    {
        if (BtnHome == null || NavHomeBg == null || _vault?.Settings == null) return;

        _activeNavButton = GetNavButtonForCurrentView() ?? _activeNavButton ?? BtnHome;
        var isDark = _vault.Settings.DarkTheme;
        var accent = GetAccentColor();
        var inactiveForeground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#777777" : "#4B5563"));
        var activeForeground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#E8E8E8" : "#111827"));
        var activeBackgroundColor = isDark
            ? BlendColor(Color.FromRgb(0x16, 0x16, 0x16), accent, 0.36)
            : BlendColor(Colors.White, accent, 0.18);
        var activeBackground = new SolidColorBrush(activeBackgroundColor);

        foreach (var (button, border) in new[]
        {
            (BtnHome, NavHomeBg),
            (BtnPasswordManager, NavPasswordBg),
            (BtnNotes, NavNotesBg),
            (BtnGraph, NavGraphBg),
            (BtnSecurity, NavSecurityBg),
            (BtnTrash, NavTrashBg),
            (BtnSettings, NavSettingsBg)
        })
        {
            border.Background = button == _activeNavButton ? activeBackground : Brushes.Transparent;
            button.Background = Brushes.Transparent;
            button.Foreground = button == _activeNavButton ? activeForeground : inactiveForeground;
            button.BorderBrush = Brushes.Transparent;
        }
    }

    private void ApplyCheckboxTheme(CheckBox? checkBox)
    {
        if (checkBox == null || _vault?.Settings == null) return;
        checkBox.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            _vault.Settings.DarkTheme ? "#999999" : "#374151"));
    }

    private void ApplyPasswordManagerTheme()
    {
        if (SearchBox == null || _vault?.Settings == null) return;

        var isDark = _vault.Settings.DarkTheme;
        var accent = GetAccentColor();
        ApplyTextBoxTheme(SearchBox, isDark, accent);
        ApplyPasswordActionButtonTheme(PasswordAddButton, "primary", isDark, accent);
        ApplyPasswordActionButtonTheme(PasswordEditButton, "ghost", isDark, accent);
        ApplyPasswordActionButtonTheme(PasswordCopyButton, "ghost", isDark, accent);
        ApplyPasswordActionButtonTheme(PasswordCopyTotpButton, "ghost", isDark, accent);
        ApplyPasswordActionButtonTheme(PasswordImportCsvButton, "ghost", isDark, accent);
        ApplyPasswordActionButtonTheme(PasswordDeleteButton, "danger", isDark, accent);
    }

    private static void ApplyTextBoxTheme(TextBox textBox, bool isDark, Color accent)
    {
        textBox.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#252525" : "#FFFFFF"));
        textBox.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#E8E8E8" : "#1F2937"));
        textBox.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#3A3A3A" : "#CBD5E1"));
        textBox.CaretBrush = new SolidColorBrush(accent);
        textBox.SelectionBrush = new SolidColorBrush(accent);
    }

    private static void ApplyPasswordActionButtonTheme(Button button, string variant, bool isDark, Color accent)
    {
        switch (variant)
        {
            case "primary":
                button.Background = new SolidColorBrush(accent);
                button.Foreground = Brushes.White;
                button.BorderBrush = new SolidColorBrush(accent);
                break;
            case "danger":
                button.Background = Brushes.Transparent;
                button.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#F87171" : "#DC2626"));
                button.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#5F1A1A" : "#FCA5A5"));
                break;
            default:
                button.Background = Brushes.Transparent;
                button.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#A3A3A3" : "#374151"));
                button.BorderBrush = new SolidColorBrush(isDark
                    ? BlendColor(Color.FromRgb(0x4A, 0x4A, 0x4A), accent, 0.18)
                    : BlendColor(Color.FromRgb(0xCB, 0xD5, 0xE1), accent, 0.24));
                break;
        }
    }

    private void ApplyThemeRecursive(DependencyObject root, Brush bgBrush, Brush panelBrush, Brush fgBrush, Brush borderBrush, bool isDark)
    {
        CaptureThemeSnapshot(root);
        RestoreThemeSnapshot(root);
        if (isDark)
        {
            var darkCount = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < darkCount; i++)
                ApplyThemeRecursive(VisualTreeHelper.GetChild(root, i), bgBrush, panelBrush, fgBrush, borderBrush, isDark);
            return;
        }

        switch (root)
        {
            case Border border:
                border.Background = ThemeBrush(border.Background, bgBrush, panelBrush, keepTransparent: true);
                border.BorderBrush = ThemeBorder(border.BorderBrush, borderBrush);
                break;
            case Panel panel:
                panel.Background = ThemeBrush(panel.Background, bgBrush, panelBrush, keepTransparent: true);
                break;
            case Control control:
                control.Background = ThemeBrush(control.Background, bgBrush, panelBrush, keepTransparent: true);
                control.BorderBrush = ThemeBorder(control.BorderBrush, borderBrush);
                control.Foreground = ThemeForeground(control.Foreground, fgBrush);
                ApplyThemeControlOverrides(control, isDark);
                break;
            case TextBlock textBlock:
                textBlock.Foreground = ThemeForeground(textBlock.Foreground, fgBrush);
                break;
            case Shape shape:
                shape.Fill = ThemeShapeFill(shape.Fill, borderBrush);
                break;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            ApplyThemeRecursive(VisualTreeHelper.GetChild(root, i), bgBrush, panelBrush, fgBrush, borderBrush, isDark);
    }

    private void CaptureThemeSnapshots(DependencyObject root)
    {
        CaptureThemeSnapshot(root);
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            CaptureThemeSnapshots(VisualTreeHelper.GetChild(root, i));
    }

    private void CaptureThemeSnapshot(DependencyObject root)
    {
        if (_themeSnapshots.ContainsKey(root)) return;

        var snapshot = new ThemeSnapshot();
        switch (root)
        {
            case Border border:
                snapshot.Background = border.Background;
                snapshot.BorderBrush = border.BorderBrush;
                break;
            case Panel panel:
                snapshot.Background = panel.Background;
                break;
            case Control control:
                snapshot.Background = control.Background;
                snapshot.Foreground = control.Foreground;
                snapshot.BorderBrush = control.BorderBrush;
                break;
            case TextBlock textBlock:
                snapshot.Foreground = textBlock.Foreground;
                break;
            case Shape shape:
                snapshot.Fill = shape.Fill;
                break;
        }

        _themeSnapshots[root] = snapshot;
    }

    private void RestoreThemeSnapshot(DependencyObject root)
    {
        if (!_themeSnapshots.TryGetValue(root, out var snapshot)) return;

        switch (root)
        {
            case Border border:
                border.Background = snapshot.Background;
                border.BorderBrush = snapshot.BorderBrush;
                break;
            case Panel panel:
                panel.Background = snapshot.Background;
                break;
            case Control control:
                control.Background = snapshot.Background;
                control.Foreground = snapshot.Foreground;
                control.BorderBrush = snapshot.BorderBrush;
                break;
            case TextBlock textBlock:
                textBlock.Foreground = snapshot.Foreground;
                break;
            case Shape shape:
                shape.Fill = snapshot.Fill;
                break;
        }
    }

    private sealed class ThemeSnapshot
    {
        public Brush? Background { get; set; }
        public Brush? Foreground { get; set; }
        public Brush? BorderBrush { get; set; }
        public Brush? Fill { get; set; }
    }

    private static void ApplyThemeControlOverrides(Control control, bool isDark)
    {
        if (control is TextBox or PasswordBox)
        {
            control.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#252525" : "#FFFFFF"));
            control.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#E8E8E8" : "#1F2937"));
            control.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#3A3A3A" : "#D8DEE8"));
        }
        else if (control is ComboBox)
        {
            control.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#252525" : "#FFFFFF"));
            control.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#E8E8E8" : "#1F2937"));
            control.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#3A3A3A" : "#D8DEE8"));
        }
        else if (control is ListView or ListBox)
        {
            control.Background = Brushes.Transparent;
            control.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#CCCCCC" : "#1F2937"));
        }
    }

    private static Brush ThemeBrush(Brush current, Brush bgBrush, Brush panelBrush, bool keepTransparent)
    {
        if (current is not SolidColorBrush solid) return current;
        if (solid.Color.A == 0 && keepTransparent) return current;
        var hex = ColorToRgbHex(solid.Color);
        return hex switch
        {
            "#161616" or "#F5F5F5" or "#F5F7FA" or "#F8FAFC" => bgBrush,
            "#141414" or "#FFFFFF" or "#1A1A1A" or "#1C1C1C" or "#1E1E1E" or "#FAFAFA" or "#252525" or "#2D2D2D" or "#E8E8E8" or "#EEF2F7" or "#F3F4F6" => panelBrush,
            _ => current
        };
    }

    private static Brush ThemeBorder(Brush current, Brush borderBrush)
    {
        if (current is not SolidColorBrush solid) return current;
        var hex = ColorToRgbHex(solid.Color);
        return hex switch
        {
            "#222222" or "#2A2A2A" or "#333333" or "#3A3A3A" or "#D8DEE8" or "#DDE3EA" or "#E3E7ED" or "#E5E7EB" => borderBrush,
            _ => current
        };
    }

    private static Brush ThemeShapeFill(Brush current, Brush borderBrush)
    {
        if (current is not SolidColorBrush solid) return current;
        var hex = ColorToRgbHex(solid.Color);
        return hex switch
        {
            "#222222" or "#2A2A2A" or "#333333" or "#D8DEE8" or "#DDE3EA" or "#E5E7EB" => borderBrush,
            _ => current
        };
    }

    private static Brush ThemeForeground(Brush current, Brush fgBrush)
    {
        if (current is not SolidColorBrush solid) return current;
        var hex = ColorToRgbHex(solid.Color);
        return hex switch
        {
            "#E8E8E8" or "#CCCCCC" or "#BBBBBB" or "#999999" or "#888888" or "#777777" or "#666666" or "#555555" or "#444444" or "#222222" or "#1F2937" or "#374151" or "#4B5563" or "#6B7280" => fgBrush,
            _ => current
        };
    }

    private static string ColorToRgbHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private void ExportEncryptedShare_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Bastion Share (*.bastion-share)|*.bastion-share",
            FileName = $"bastion-share-{DateTime.Now:yyyyMMdd-HHmm}.bastion-share"
        };
        if (dlg.ShowDialog() != true) return;

        var share = new
        {
            createdAt = DateTime.UtcNow,
            expiresAt = DateTime.UtcNow.AddDays(7),
            entries = _vault.Entries,
            notes = _vault.Notes
        };
        var json = System.Text.Json.JsonSerializer.Serialize(share);
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(_password));
        var cipher = SecureVault.Crypto.CryptoService.Encrypt(json, key, out var nonce, out var tag);
        var payload = new
        {
            version = 1,
            algorithm = "AES-256-GCM",
            expiresAt = DateTime.UtcNow.AddDays(7),
            nonce = Convert.ToBase64String(nonce),
            tag = Convert.ToBase64String(tag),
            cipher = Convert.ToBase64String(cipher)
        };
        File.WriteAllText(dlg.FileName, System.Text.Json.JsonSerializer.Serialize(payload));
        new BastionDialog("Encrypted share exported", "The .bastion-share file is encrypted with your master password. To import it later, open Settings, choose Import encrypted share, select the file, and enter the password used for this export. The 7-day expiry is metadata inside the file.", false).ShowDialog();
    }

    private void ImportEncryptedShare_Click(object sender, RoutedEventArgs e)
    {
        var openDlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Bastion Share (*.bastion-share)|*.bastion-share"
        };
        if (openDlg.ShowDialog() != true) return;

        var passwordDlg = new PasswordPromptDialog(
            "Import encrypted share",
            "Enter the password used when this .bastion-share file was exported.") { Owner = this };
        if (passwordDlg.ShowDialog() != true || string.IsNullOrWhiteSpace(passwordDlg.Password))
            return;

        try
        {
            var payloadJson = File.ReadAllText(openDlg.FileName);
            var payload = System.Text.Json.JsonSerializer.Deserialize<EncryptedSharePayload>(
                payloadJson, ShareJsonOptions);
            if (payload == null || payload.Version != 1 ||
                string.IsNullOrWhiteSpace(payload.Nonce) ||
                string.IsNullOrWhiteSpace(payload.Tag) ||
                string.IsNullOrWhiteSpace(payload.Cipher))
            {
                new BastionDialog("Import failed", "This file is not a valid Bastion encrypted share.", false).ShowDialog();
                return;
            }

            if (payload.ExpiresAt < DateTime.UtcNow)
            {
                var expired = new BastionDialog("Share expired",
                    $"This share expired on {payload.ExpiresAt:g}. Import it anyway?", true) { Owner = this };
                if (expired.ShowDialog() != true)
                    return;
            }

            var key = SHA256.HashData(Encoding.UTF8.GetBytes(passwordDlg.Password));
            var json = SecureVault.Crypto.CryptoService.Decrypt(
                Convert.FromBase64String(payload.Cipher),
                key,
                Convert.FromBase64String(payload.Nonce),
                Convert.FromBase64String(payload.Tag));
            var content = System.Text.Json.JsonSerializer.Deserialize<EncryptedShareContent>(
                json, ShareJsonOptions);
            if (content == null)
            {
                new BastionDialog("Import failed", "The share decrypted but did not contain importable vault data.", false).ShowDialog();
                return;
            }

            var (passwordsImported, passwordsSkipped, notesImported, notesSkipped) = MergeEncryptedShare(content);
            SyncVaultTags();
            VaultStore.Save(_vault, _password);
            RefreshPasswordList();
            RefreshNotesTree();
            RefreshTagPanel();
            UpdateHomeStats();

            new BastionDialog("Encrypted share imported",
                $"Imported {passwordsImported} password(s) and {notesImported} note(s).\nSkipped {passwordsSkipped} duplicate password(s) and {notesSkipped} duplicate note(s).",
                false).ShowDialog();
        }
        catch (CryptographicException)
        {
            new BastionDialog("Import failed", "The share could not be decrypted. Check that you entered the password used when it was exported.", false).ShowDialog();
        }
        catch (FormatException)
        {
            new BastionDialog("Import failed", "The selected file is not a valid Bastion encrypted share.", false).ShowDialog();
        }
        catch (Exception ex)
        {
            new BastionDialog("Import failed", ex.Message, false).ShowDialog();
        }
    }

    private (int passwordsImported, int passwordsSkipped, int notesImported, int notesSkipped) MergeEncryptedShare(EncryptedShareContent content)
    {
        var passwordsImported = 0;
        var passwordsSkipped = 0;
        var notesImported = 0;
        var notesSkipped = 0;

        foreach (var entry in content.Entries ?? new List<VaultEntry>())
        {
            entry.Tags ??= new List<string>();
            if (_vault.Entries.Any(existing =>
                    string.Equals(existing.Url, entry.Url, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.Username, entry.Username, StringComparison.OrdinalIgnoreCase) &&
                    existing.Password == entry.Password))
            {
                passwordsSkipped++;
                continue;
            }

            if (_vault.Entries.Any(existing => existing.Id == entry.Id))
                entry.Id = Guid.NewGuid().ToString();
            _vault.Entries.Add(entry);
            passwordsImported++;
        }

        foreach (var note in content.Notes ?? new List<SecureNote>())
        {
            note.Tags ??= new List<string>();
            note.History ??= new List<NoteSnapshot>();
            note.Attachments ??= new List<NoteAttachment>();
            if (_vault.Notes.Any(existing =>
                    string.Equals(existing.Title, note.Title, StringComparison.OrdinalIgnoreCase) &&
                    existing.Body == note.Body))
            {
                notesSkipped++;
                continue;
            }

            if (_vault.Notes.Any(existing => existing.Id == note.Id))
                note.Id = Guid.NewGuid().ToString();
            _vault.Notes.Add(note);
            notesImported++;
        }

        return (passwordsImported, passwordsSkipped, notesImported, notesSkipped);
    }

    private static readonly System.Text.Json.JsonSerializerOptions ShareJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private class EncryptedSharePayload
    {
        public int Version { get; set; }
        public string Algorithm { get; set; } = "";
        public DateTime ExpiresAt { get; set; }
        public string Nonce { get; set; } = "";
        public string Tag { get; set; } = "";
        public string Cipher { get; set; } = "";
    }

    private class EncryptedShareContent
    {
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public List<VaultEntry> Entries { get; set; } = new();
        public List<SecureNote> Notes { get; set; } = new();
    }

    // ---- TAGS ----

    private void AddTagToNote_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNote == null) return;
        var dlg = new TagInputDialog(_vault.Tags) { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.TagName))
        {
            var tag = dlg.TagName.Trim().ToLower();
            if (!_currentNote.Tags.Contains(tag)) _currentNote.Tags.Add(tag);
            if (!_vault.Tags.Contains(tag)) _vault.Tags.Add(tag);
            VaultStore.Save(_vault, _password);
            RefreshNoteTags();
            RefreshTagPanel();
        }
    }

    private void RefreshNoteTags()
    {
        if (NoteTagsPanel == null || _currentNote == null) return;
        NoteTagsPanel.Children.Clear();
        foreach (var tag in _currentNote.Tags)
        {
            var chip = BuildTagChip(tag, () =>
            {
                _currentNote.Tags.Remove(tag);
                SyncVaultTags();
                VaultStore.Save(_vault, _password);
                RefreshNoteTags();
                RefreshTagPanel();
            });
            NoteTagsPanel.Children.Add(chip);
        }
    }

    private void AttachFile_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNote == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true };
        if (dlg.ShowDialog() == true)
            AddAttachments(dlg.FileNames);
    }

    private void NoteBodyEditor_Drop(object sender, DragEventArgs e)
    {
        if (_currentNote == null) return;
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            AddAttachments(files);
            e.Handled = true;
        }
    }

    private void NoteBodyEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_currentNote == null) return;
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
            TryPasteAttachmentFromClipboard())
        {
            e.Handled = true;
        }
    }

    private bool TryPasteAttachmentFromClipboard()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().Where(File.Exists).ToList();
                if (files.Count == 0) return false;
                AddAttachments(files);
                return true;
            }

            if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image == null) return false;
                var fileName = $"pasted-image-{DateTime.Now:yyyyMMdd-HHmmss}.png";
                AddImageAttachment(image, fileName);
                return true;
            }
        }
        catch (Exception ex)
        {
            NoteSaveStatus.Text = $"Paste failed: {ex.Message}";
        }

        return false;
    }

    private void AddAttachments(IEnumerable<string> fileNames)
    {
        if (_currentNote == null) return;
        var added = false;
        foreach (var fileName in fileNames.Where(File.Exists))
        {
            var info = new FileInfo(fileName);
            if (info.Length > MaxAttachmentBytes)
            {
                new BastionDialog("Attachment too large",
                    $"{info.Name} is larger than {FormatFileSize(MaxAttachmentBytes)}. Store smaller files in Bastion to keep the vault responsive.",
                    false) { Owner = this }.ShowDialog();
                continue;
            }

            var data = File.ReadAllBytes(fileName);
            AddAttachmentBytes(info.Name, GetContentType(fileName), data);
            added = true;
        }
        if (added)
        {
            SaveCurrentNote();
            RefreshAttachments();
        }
    }

    private void AddImageAttachment(BitmapSource source, string fileName)
    {
        if (_currentNote == null) return;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        AddAttachmentBytes(fileName, "image/png", stream.ToArray());
        SaveCurrentNote();
        RefreshAttachments();
    }

    private void AddAttachmentBytes(string fileName, string contentType, byte[] data)
    {
        if (_currentNote == null) return;
        _currentNote.Attachments.Add(new NoteAttachment
        {
            FileName = string.IsNullOrWhiteSpace(fileName) ? "attachment" : fileName,
            ContentType = contentType,
            DataBase64 = Convert.ToBase64String(data)
        });
    }

    private void RefreshAttachments()
    {
        if (AttachmentsList == null) return;
        AttachmentsList.ItemsSource = _currentNote?.Attachments
            .Select(a => new AttachmentView(
                a.Id,
                string.IsNullOrWhiteSpace(a.FileName) ? "Attachment" : a.FileName,
                $"{AttachmentKind(a)} - {FormatFileSize(GetAttachmentSizeBytes(a))}"))
            .ToList();
        RefreshInlineAttachments();
    }

    private void RefreshInlineAttachments()
    {
        if (InlineAttachmentsPanel == null || InlineAttachmentsHost == null) return;

        InlineAttachmentsPanel.Children.Clear();
        var attachments = _currentNote?.Attachments ?? new List<NoteAttachment>();
        InlineAttachmentsHost.Visibility = attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var attachment in attachments)
            InlineAttachmentsPanel.Children.Add(BuildInlineAttachmentCard(attachment));
    }

    private Border BuildInlineAttachmentCard(NoteAttachment attachment)
    {
        var card = new Border
        {
            Width = attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? 300 : 240,
            MinHeight = 92,
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(10),
            Background = new SolidColorBrush(BlendColor(Color.FromRgb(0x16, 0x16, 0x16), GetAccentColor(), 0.08)),
            BorderBrush = new SolidColorBrush(BlendColor(Color.FromRgb(0x2D, 0x2D, 0x2D), GetAccentColor(), 0.18)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };

        var stack = new StackPanel();
        if (attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
            TryLoadAttachmentImage(attachment, out var imageSource))
        {
            stack.Children.Add(new Border
            {
                Height = 190,
                Background = Brushes.Black,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = new Image
                {
                    Source = imageSource,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(4)
                }
            });
        }
        else
        {
            stack.Children.Add(new TextBlock
            {
                Text = AttachmentKind(attachment),
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(GetAccentColor()),
                Margin = new Thickness(0, 0, 0, 12)
            });
        }

        stack.Children.Add(new TextBlock
        {
            Text = attachment.FileName,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 2)
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"{AttachmentKind(attachment)} - {FormatFileSize(GetAttachmentSizeBytes(attachment))}",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
            Margin = new Thickness(0, 0, 0, 8)
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var open = new Button
        {
            Content = "Open",
            Style = (Style)FindResource("GhostBtn"),
            Height = 24,
            FontSize = 10,
            Padding = new Thickness(8, 0, 8, 0),
            Tag = attachment.Id,
            Margin = new Thickness(0, 0, 6, 0)
        };
        open.Click += OpenAttachment_Click;

        var remove = new Button
        {
            Content = "Remove",
            Style = (Style)FindResource("DangerBtn"),
            Height = 24,
            FontSize = 10,
            Padding = new Thickness(8, 0, 8, 0),
            Tag = attachment.Id
        };
        remove.Click += RemoveAttachment_Click;

        actions.Children.Add(open);
        actions.Children.Add(remove);
        stack.Children.Add(actions);
        card.Child = stack;
        return card;
    }

    private static bool TryLoadAttachmentImage(NoteAttachment attachment, out BitmapImage? image)
    {
        image = null;
        try
        {
            var bytes = Convert.FromBase64String(attachment.DataBase64);
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            image = bitmap;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void Attachment_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_currentNote == null || AttachmentsList.SelectedItem is not AttachmentView selected) return;
        OpenAttachment(selected.Id);
    }

    private void OpenAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string id)
            OpenAttachment(id);
    }

    private void OpenAttachment(string id)
    {
        if (_currentNote == null) return;
        var attachment = _currentNote.Attachments.FirstOrDefault(a => a.Id == id);
        if (attachment == null) return;
        var safeName = MakeSafeFileName(attachment.FileName);
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Bastion", "Attachments");
        Directory.CreateDirectory(tempDir);
        var tempPath = System.IO.Path.Combine(tempDir, $"{attachment.Id}-{safeName}");
        File.WriteAllBytes(tempPath, Convert.FromBase64String(attachment.DataBase64));
        Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNote == null || ((FrameworkElement)sender).Tag is not string id) return;
        var attachment = _currentNote.Attachments.FirstOrDefault(a => a.Id == id);
        if (attachment == null) return;

        var dlg = new BastionDialog($"Remove \"{attachment.FileName}\"?",
            "This removes the encrypted attachment from the note and deletes its attachment link from the editor.",
            true) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        _currentNote.Attachments.Remove(attachment);
        NoteBodyEditor.Text = StripAttachmentLinks(NoteBodyEditor.Text);
        SaveCurrentNote();
        RefreshAttachments();
    }

    private static string AttachmentKind(NoteAttachment attachment)
        => attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "Image" : "File";

    private static long GetAttachmentSizeBytes(NoteAttachment attachment)
    {
        try { return Convert.FromBase64String(attachment.DataBase64 ?? "").LongLength; }
        catch { return 0; }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0.#} KB";
        return $"{kb / 1024.0:0.#} MB";
    }

    private static string MakeSafeFileName(string fileName)
    {
        var safe = string.Join("_", (fileName ?? "attachment").Split(System.IO.Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(safe) ? "attachment" : safe;
    }

    private sealed record AttachmentView(string Id, string FileName, string Details);

    private static string GetContentType(string fileName)
    {
        var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream"
        };
    }

    private static string CompressText(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text ?? "");
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
            gzip.Write(bytes, 0, bytes.Length);
        return Convert.ToBase64String(output.ToArray());
    }

    private Border BuildTagChip(string tag, Action? onRemove = null, Action? onClick = null)
    {
        var tagColor = GetTagColor(tag);
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var swatch = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(tagColor),
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "Change tag color",
            Cursor = Cursors.Hand
        };
        swatch.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            ChangeTagColor(tag);
        };
        sp.Children.Add(swatch);
        sp.Children.Add(new TextBlock
        {
            Text = tag,
            FontSize = 11,
            Foreground = new SolidColorBrush(BlendColor(Colors.White, tagColor, 0.28)),
            VerticalAlignment = VerticalAlignment.Center
        });
        if (onRemove != null)
        {
            var x = new TextBlock { Text = "  Remove", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0xA3, 0xA3, 0xA3)), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand };
            x.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                onRemove();
            };
            sp.Children.Add(x);
        }
        var chip = new Border
        {
            Background = new SolidColorBrush(BlendColor(Color.FromRgb(0x1A, 0x1A, 0x1A), tagColor, 0.24)),
            BorderBrush = new SolidColorBrush(BlendColor(Color.FromRgb(0x33, 0x33, 0x33), tagColor, 0.7)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 4, 4),
            Child = sp, Cursor = Cursors.Hand
        };
        chip.MouseLeftButtonDown += (_, _) =>
        {
            if (onClick != null) onClick();
            else
            {
                _activeTagFilter = tag;
                RefreshNotesTree();
            }
        };
        return chip;
    }

    private Color GetTagColor(string tag)
    {
        if (_vault?.Settings?.TagColors != null &&
            _vault.Settings.TagColors.TryGetValue(tag, out var hex) &&
            TryParseHexColor(hex, out var stored))
            return stored;

        var hash = unchecked((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(tag));
        var hue = hash % 360;
        return ColorFromHsv(hue, 0.72, 0.92);
    }

    private void ChangeTagColor(string tag)
    {
        if (_vault?.Settings == null) return;
        var selected = ShowColorMapDialog($"Tag color: {tag}", GetTagColor(tag));
        if (selected == null) return;

        _vault.Settings.TagColors ??= new Dictionary<string, string>();
        _vault.Settings.TagColors[tag] = ColorToHex(selected.Value);
        VaultStore.Save(_vault, _password);
        RefreshNoteTags();
        RefreshTagPanel();
        RefreshPasswordTagsPanel();
    }

    private Color? ShowColorMapDialog(string title, Color initial)
    {
        EnsureSettingsColorMaps();
        var selected = initial;
        var updating = false;

        var preview = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(6),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(selected),
            Margin = new Thickness(0, 0, 8, 0)
        };
        var hexBox = new TextBox
        {
            Text = ColorToHex(selected),
            Height = 30,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
            CaretBrush = new SolidColorBrush(GetAccentColor()),
            SelectionBrush = new SolidColorBrush(GetAccentColor()),
            Padding = new Thickness(10, 0, 10, 0)
        };
        hexBox.TextChanged += (_, _) =>
        {
            if (updating || !TryParseHexColor(hexBox.Text, out var typed)) return;
            selected = typed;
            preview.Background = new SolidColorBrush(selected);
        };

        var map = new Image
        {
            Source = _colorMapSource,
            Stretch = Stretch.Fill,
            Cursor = Cursors.Cross,
            Height = 130
        };
        void Pick(Point p)
        {
            selected = ColorFromMapPoint(p, Math.Max(map.ActualWidth, 1), Math.Max(map.ActualHeight, 1));
            preview.Background = new SolidColorBrush(selected);
            updating = true;
            hexBox.Text = ColorToHex(selected);
            updating = false;
        }
        map.MouseLeftButtonDown += (_, e) => { map.CaptureMouse(); Pick(e.GetPosition(map)); e.Handled = true; };
        map.MouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) Pick(e.GetPosition(map)); };
        map.MouseLeftButtonUp += (_, _) => map.ReleaseMouseCapture();

        var dialog = new Window
        {
            Owner = this,
            Title = title,
            Width = 360,
            Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            ResizeMode = ResizeMode.NoResize,
            Background = Brushes.Transparent
        };

        var root = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
            BorderThickness = new Thickness(1)
        };
        var stack = new StackPanel { Margin = new Thickness(18) };
        var titleBar = new DockPanel { Margin = new Thickness(0, 0, 0, 14), Height = 26 };
        titleBar.MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == MouseButton.Left) dialog.DragMove(); };
        titleBar.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        var topRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        topRow.Children.Add(preview);
        topRow.Children.Add(hexBox);

        var mapBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            ClipToBounds = true,
            Child = map
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("GhostBtn"), Width = 86, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
        var apply = new Button { Content = "Apply", Style = (Style)FindResource("ToolbarBtn"), Width = 86, Height = 30 };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        apply.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(apply);

        stack.Children.Add(titleBar);
        stack.Children.Add(topRow);
        stack.Children.Add(mapBorder);
        stack.Children.Add(actions);
        root.Child = stack;
        dialog.Content = root;

        return dialog.ShowDialog() == true ? selected : null;
    }

    private void FilterByTag(string tag)
    {
        _activeTagFilter = _activeTagFilter == tag ? null : tag;
        RefreshNotesTree();
    }

    private void RefreshAllTags()
    {
        if (AllTagsPanel == null) return;
        AllTagsPanel.Children.Clear();
        var allTags = _vault.Tags.OrderBy(t => t).ToList();
        foreach (var tag in allTags)
        {
            var t = tag;
            var chip = BuildTagChip(t, null, () => { FilterByTag(t); RefreshAllTags(); });
            if (_activeTagFilter == t)
            {
                chip.Background = new SolidColorBrush(BlendColor(Color.FromRgb(0x16, 0x16, 0x16), GetTagColor(t), 0.44));
                chip.BorderThickness = new Thickness(2);
            }
            AllTagsPanel.Children.Add(chip);
        }
        if (allTags.Count == 0)
            AllTagsPanel.Children.Add(new TextBlock { Text = "No tags yet", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)) });
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowExit && _vault?.Settings?.RunInTray == true)
        {
            e.Cancel = true;
            SaveCurrentNote();
            HideToTray("Bastion is still running in the tray. Browser autofill stays available until the vault locks.");
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
        base.OnClosed(e);
    }
}
