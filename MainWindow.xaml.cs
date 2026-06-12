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
    private bool _isEditMode = true;
    private bool _suppressNoteChange = false;
    private string _currentSortMode = "LastEdited";

    // Browser autofill API
    private BastionLocalApi? _localApi;

    // Tag filter state
    private string? _activeTagFilter = null;
    private string? _activePasswordTagFilter = null;
    private bool _isUpdatingSettingsControls = false;
    private Button? _activeNavButton;
    private readonly Dictionary<DependencyObject, ThemeSnapshot> _themeSnapshots = new();

    // Graph
    private double _graphOffsetX = 0, _graphOffsetY = 0, _graphScale = 1.0;
    private bool _isPanning;
    private Point _panStart;
    private Ellipse? _draggingNode;
    private Point _nodeDragOffset;
    private readonly Dictionary<Ellipse, SecureNote> _nodeMap = new();
    private readonly Dictionary<SecureNote, (double x, double y)> _nodePositions = new();
    // Smooth graph animation state — declared in graph region below

    public MainWindow(Vault vault, string password)
    {
        InitializeComponent();
        _vault = vault;
        _password = password;

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
        UpdateSettingsControls();
        VaultList.ItemsSource = _vault.Entries;

        MouseMove += (_, _) => ResetLockTimer();
        KeyDown += (_, _) => ResetLockTimer();

        // Start local API bridge for browser extension
        _localApi = new BastionLocalApi(_vault, () => Dispatcher.Invoke(() =>
        {
            VaultStore.Save(_vault, _password);
            RefreshPasswordList();
            UpdateHomeStats();
        }));
        _localApi.Start();

        LoadGraphColors();
        ApplyTheme();
        UpdateGreeting();
        ShowHome();
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
    }
    private void CloseApp_Click(object sender, RoutedEventArgs e) { SaveCurrentNote(); Close(); }

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
        new LoginWindow().Show();
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
        NoteSaveStatus.Text = $"{label} copied - clears in 15s";
        await Task.Delay(15000);
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
            });
            chip.Background = _activePasswordTagFilter == tag
                ? new SolidColorBrush(Color.FromRgb(0x3A, 0x25, 0x66))
                : new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
            chip.MouseLeftButtonDown += (_, _) =>
            {
                _activePasswordTagFilter = _activePasswordTagFilter == tag ? null : tag;
                RefreshPasswordList();
            };
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
        var ctx = new ContextMenu { Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)) };

        var moveHeader = new MenuItem { Header = "Move to folder", IsEnabled = false, Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), FontSize = 11 };
        ctx.Items.Add(moveHeader);
        ctx.Items.Add(new Separator());

        // No folder option
        var noFolder = new MenuItem { Header = "(No folder)", Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), Background = Brushes.Transparent };
        noFolder.Click += (_, _) => { note.Folder = ""; VaultStore.Save(_vault, _password); RefreshNotesTree(); };
        ctx.Items.Add(noFolder);

        // Existing folders
        foreach (var folder in _vault.Notes.Where(n => !string.IsNullOrEmpty(n.Folder)).Select(n => n.Folder).Distinct().OrderBy(f => f))
        {
            var f = folder;
            var mi = new MenuItem { Header = "📁 " + f, Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), Background = Brushes.Transparent };
            mi.Click += (_, _) => { note.Folder = f; VaultStore.Save(_vault, _password); RefreshNotesTree(); };
            ctx.Items.Add(mi);
        }

        // New folder option
        ctx.Items.Add(new Separator());
        var newFolderMi = new MenuItem { Header = "+ New folder…", Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED)), Background = Brushes.Transparent };
        newFolderMi.Click += (_, _) =>
        {
            var dlg = new FolderNameDialog { Owner = Application.Current.MainWindow };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            { note.Folder = dlg.FolderName; VaultStore.Save(_vault, _password); RefreshNotesTree(); }
        };
        ctx.Items.Add(newFolderMi);

        item.ContextMenu = ctx;
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
        if (NotesTree.SelectedItem is ListBoxItem { Tag: SecureNote note })
            OpenNote(note);
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
        NoteBodyEditor.Text = note.Body;
        NoteEditorTitle.Text = note.Title;
        NoteCreatedText.Text = note.CreatedAt.ToString("MMM d, yyyy");
        NoteModifiedText.Text = note.UpdatedAt.ToString("MMM d, yyyy HH:mm");
        NoteFolderText.Text = string.IsNullOrEmpty(note.Folder) ? "—" : note.Folder;
        UpdateWordCount(); UpdateOutline();
        RefreshNoteTags();
        RefreshAttachments();
        if (!_isEditMode) RenderPreview();
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
        var oldBody = _currentNote.Body ?? "";
        var newBody = NoteBodyEditor.Text ?? "";
        if (Math.Abs(newBody.Length - oldBody.Length) > 10 || (_currentNote.History.Count == 0 && newBody.Length > 0))
        {
            _currentNote.History.Add(new NoteSnapshot
            {
                SavedAt = DateTime.Now,
                Body = "",
                CompressedBodyBase64 = CompressText(oldBody),
                Title = _currentNote.Title,
                IsFullCopy = true
            });
            // Keep last 50 snapshots
            if (_currentNote.History.Count > 50)
                _currentNote.History.RemoveAt(0);
        }

        _currentNote.Title = NoteTitleEditor.Text.Trim().Length > 0 ? NoteTitleEditor.Text.Trim() : "Untitled";
        _currentNote.Body = newBody;
        _currentNote.UpdatedAt = DateTime.Now;
        VaultStore.Save(_vault, _password);
        if (NotesTree != null) RefreshNotesTree();
        UpdateHomeStats();
        UpdateBacklinks();
        NoteSaveStatus.Text = $"Saved · {DateTime.Now:HH:mm:ss}";
        NoteModifiedText.Text = _currentNote.UpdatedAt.ToString("MMM d, yyyy HH:mm");
    }

    private void DeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNote == null) return;
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

    private void OutlineList_Click(object sender, SelectionChangedEventArgs e) { if (!_isEditMode) SwitchToEdit_Click(sender, new RoutedEventArgs()); }

    private void SwitchToEdit_Click(object sender, RoutedEventArgs e)
    { _isEditMode = true; EditPanel.Visibility = Visibility.Visible; PreviewPanel.Visibility = Visibility.Collapsed; BtnEditMode.Style = (Style)FindResource("ToolbarBtn"); BtnPreviewMode.Style = (Style)FindResource("GhostBtn"); }

    private void SwitchToPreview_Click(object sender, RoutedEventArgs e)
    { _isEditMode = false; SaveCurrentNote(); RenderPreview(); EditPanel.Visibility = Visibility.Collapsed; PreviewPanel.Visibility = Visibility.Visible; BtnEditMode.Style = (Style)FindResource("GhostBtn"); BtnPreviewMode.Style = (Style)FindResource("ToolbarBtn"); }

    private void RenderPreview() { PreviewBox.Document.Blocks.Clear(); RenderMarkdownToRichText(NoteBodyEditor.Text ?? "", PreviewBox.Document); }

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
            var bytes = Convert.FromBase64String(attachment.DataBase64);
            var image = new BitmapImage();
            using var stream = new MemoryStream(bytes);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            doc.Blocks.Add(new BlockUIContainer(new Image { Source = image, MaxWidth = 520, Margin = new Thickness(0, 8, 0, 8) }));
            return true;
        }
        doc.Blocks.Add(MakePara($"Attachment: {attachment.FileName}", 13, FontWeights.Normal, "#AAAAAA", 4, 4));
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

        // Connection counts
        var connCount = notes.ToDictionary(n => n, _ => 0);
        foreach (var a in notes)
            foreach (var b in notes)
                if (a != b && (a.Body ?? "").Contains(b.Title ?? "", StringComparison.OrdinalIgnoreCase))
                { connCount[a]++; connCount[b]++; }

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
            foreach (var a in notes)
                foreach (var b in notes)
                    if (a != b && (a.Body ?? "").Contains(b.Title ?? "", StringComparison.OrdinalIgnoreCase))
                    {
                        var (x1,y1) = _nodePositions[a]; var (x2,y2) = _nodePositions[b];
                        double dx = x2-x1, dy = y2-y1;
                        double dist = Math.Max(Math.Sqrt(dx*dx+dy*dy), 1);
                        double f = dist / 220.0;
                        forces[a] = (forces[a].fx + dx/dist*f, forces[a].fy + dy/dist*f);
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
        DrawAllLines(notes, connCount);

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

    private void DrawAllLines(List<SecureNote> notes, Dictionary<SecureNote, int> connCount)
    {
        var referenced = new HashSet<(SecureNote, SecureNote)>();

        // Draw reference lines (bright)
        foreach (var a in notes)
            foreach (var b in notes)
            {
                if (a == b) continue;
                var key = a.GetHashCode() < b.GetHashCode() ? (a, b) : (b, a);
                if (referenced.Contains(key)) continue;
                bool linked = (a.Body ?? "").Contains(b.Title ?? "", StringComparison.OrdinalIgnoreCase)
                           || (b.Body ?? "").Contains(a.Title ?? "", StringComparison.OrdinalIgnoreCase);
                if (!linked) continue;
                referenced.Add(key);
                if (!_nodePositions.ContainsKey(a) || !_nodePositions.ContainsKey(b)) continue;
                var (x1,y1) = _nodePositions[a]; var (x2,y2) = _nodePositions[b];
                var line = new Line { X1=x1,Y1=y1,X2=x2,Y2=y2, Stroke=new SolidColorBrush(GraphLineColor), StrokeThickness=1.4, Opacity=0.9 };
                Panel.SetZIndex(line, -1);
                GraphInnerCanvas.Children.Add(line);
            }

        // Always draw a faint proximity web so graph is never empty
        // Connect each node to its 2 nearest neighbours with a dim line
        foreach (var a in notes)
        {
            if (!_nodePositions.ContainsKey(a)) continue;
            var (ax, ay) = _nodePositions[a];
            var nearest = notes
                .Where(b => b != a && _nodePositions.ContainsKey(b))
                .OrderBy(b => { var (bx,by) = _nodePositions[b]; return (bx-ax)*(bx-ax)+(by-ay)*(by-ay); })
                .Take(2);
            foreach (var b in nearest)
            {
                var key = a.GetHashCode() < b.GetHashCode() ? (a,b) : (b,a);
                if (referenced.Contains(key)) continue; // don't overdraw reference lines
                var (bx,by) = _nodePositions[b];
                var faint = new Line
                {
                    X1=ax,Y1=ay,X2=bx,Y2=by,
                    Stroke = new SolidColorBrush(Color.FromArgb(45, GraphLineColor.R, GraphLineColor.G, GraphLineColor.B)),
                    StrokeThickness = 0.8
                };
                Panel.SetZIndex(faint, -2);
                GraphInnerCanvas.Children.Add(faint);
            }
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
        var connCount = notes.ToDictionary(n => n, _ => 0);
        DrawAllLines(notes, connCount);
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
        SecurityList.ItemsSource = new[] { new { Title = "Checking...", Username = "", StrengthLabel = "Using k-anonymity SHA-1 prefix lookup" } };
        var results = new List<object>();
        foreach (var entry in _vault.Entries.Where(e => !string.IsNullOrEmpty(e.Password)))
        {
            var count = await SecurityInsights.CheckBreachAsync(entry.Password);
            if (count > 0)
                results.Add(new { entry.Title, entry.Username, StrengthLabel = $"Seen {count:N0} times" });
            else if (count == -1)
                results.Add(new { entry.Title, entry.Username, StrengthLabel = "Check failed" });
        }
        SecurityList.ItemsSource = results.Count > 0
            ? results
            : new[] { new { Title = "No breached passwords found", Username = "", StrengthLabel = "" } };
    }

    private void ShowWeakList(System.Collections.Generic.List<VaultEntry> entries)
    {
        SecurityList.ItemsSource = entries.Select(e2 => new
        {
            e2.Title, e2.Username,
            StrengthLabel = $"{SecurityInsights.StrengthLabel(SecurityInsights.PasswordStrength(e2.Password))} ({SecurityInsights.PasswordStrength(e2.Password)}%)"
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

    private static string ColorToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
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
            "Export encrypted share creates a .bastion-share file containing a copy of the current passwords and notes.\n\nTo import one, click Import encrypted share, choose the .bastion-share file, then enter the password that was used when the file was exported. Bastion decrypts the file and merges non-duplicate passwords and notes into the open vault.\n\nThe expiry date is stored inside the file as metadata. Expired shares show a warning before import.",
            false).ShowDialog();
    }

    private void UpdateSettingsControls()
    {
        _isUpdatingSettingsControls = true;
        try
        {
            if (AutofillEnabledCheck != null) AutofillEnabledCheck.IsChecked = _vault.Settings.AutofillEnabled;
            if (DarkThemeCheck != null) DarkThemeCheck.IsChecked = _vault.Settings.DarkTheme;
            if (AccentColorBox != null) AccentColorBox.Text = _vault.Settings.AccentColor;
            if (LockTimeoutCombo != null)
                SelectLockTimeoutItem(_vault.Settings.LockMinutes);
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

    private void AutofillEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_vault?.Settings == null || AutofillEnabledCheck == null || _isUpdatingSettingsControls) return;
        _vault.Settings.AutofillEnabled = AutofillEnabledCheck.IsChecked == true;
        VaultStore.Save(_vault, _password);
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
        if (_vault?.Settings == null || AccentColorBox == null || _isUpdatingSettingsControls || string.IsNullOrWhiteSpace(AccentColorBox.Text)) return;
        _vault.Settings.AccentColor = AccentColorBox.Text.Trim();
        ApplyTheme();
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
        ApplyNavigationTheme();
        ApplyCheckboxTheme(AutofillEnabledCheck);
        ApplyCheckboxTheme(DarkThemeCheck);
        ApplyPasswordManagerTheme();
    }

    private void ApplyNavigationTheme()
    {
        if (BtnHome == null || NavHomeBg == null || _vault?.Settings == null) return;

        _activeNavButton = GetNavButtonForCurrentView() ?? _activeNavButton ?? BtnHome;
        var isDark = _vault.Settings.DarkTheme;
        var inactiveForeground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#777777" : "#4B5563"));
        var activeForeground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#E8E8E8" : "#111827"));
        var activeBackground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#2D2D2D" : "#E5E7EB"));

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
        ApplyTextBoxTheme(SearchBox, isDark);
        ApplyPasswordActionButtonTheme(PasswordAddButton, "primary", isDark);
        ApplyPasswordActionButtonTheme(PasswordEditButton, "ghost", isDark);
        ApplyPasswordActionButtonTheme(PasswordCopyButton, "ghost", isDark);
        ApplyPasswordActionButtonTheme(PasswordCopyTotpButton, "ghost", isDark);
        ApplyPasswordActionButtonTheme(PasswordImportCsvButton, "ghost", isDark);
        ApplyPasswordActionButtonTheme(PasswordDeleteButton, "danger", isDark);
    }

    private static void ApplyTextBoxTheme(TextBox textBox, bool isDark)
    {
        textBox.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#252525" : "#FFFFFF"));
        textBox.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#E8E8E8" : "#1F2937"));
        textBox.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#3A3A3A" : "#CBD5E1"));
        textBox.CaretBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7C3AED"));
        textBox.SelectionBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7C3AED"));
    }

    private static void ApplyPasswordActionButtonTheme(Button button, string variant, bool isDark)
    {
        switch (variant)
        {
            case "primary":
                button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7C3AED"));
                button.Foreground = Brushes.White;
                button.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7C3AED"));
                break;
            case "danger":
                button.Background = Brushes.Transparent;
                button.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#F87171" : "#DC2626"));
                button.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#5F1A1A" : "#FCA5A5"));
                break;
            default:
                button.Background = Brushes.Transparent;
                button.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#A3A3A3" : "#374151"));
                button.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isDark ? "#4A4A4A" : "#CBD5E1"));
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
                VaultStore.Save(_vault, _password);
                RefreshNoteTags();
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
            AddAttachments(files);
    }

    private void AddAttachments(IEnumerable<string> fileNames)
    {
        if (_currentNote == null) return;
        foreach (var fileName in fileNames.Where(File.Exists))
        {
            var data = File.ReadAllBytes(fileName);
            var attachment = new NoteAttachment
            {
                FileName = System.IO.Path.GetFileName(fileName),
                ContentType = GetContentType(fileName),
                DataBase64 = Convert.ToBase64String(data)
            };
            _currentNote.Attachments.Add(attachment);
            NoteBodyEditor.AppendText($"\n[attachment:{attachment.FileName}](bastion-attachment://{attachment.Id})\n");
        }
        SaveCurrentNote();
        RefreshAttachments();
    }

    private void RefreshAttachments()
    {
        if (AttachmentsList == null) return;
        AttachmentsList.ItemsSource = _currentNote?.Attachments
            .Select(a => $"{a.FileName} ({Math.Round(a.DataBase64.Length * 0.75 / 1024.0, 1)} KB)")
            .ToList();
    }

    private void Attachment_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_currentNote == null || AttachmentsList.SelectedIndex < 0) return;
        var attachment = _currentNote.Attachments[AttachmentsList.SelectedIndex];
        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), attachment.FileName);
        File.WriteAllBytes(tempPath, Convert.FromBase64String(attachment.DataBase64));
        Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
    }

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

    private Border BuildTagChip(string tag, Action? onRemove = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = tag, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xAA, 0xFF)), VerticalAlignment = VerticalAlignment.Center });
        if (onRemove != null)
        {
            var x = new TextBlock { Text = " ×", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand };
            x.MouseLeftButtonDown += (_, _) => onRemove();
            sp.Children.Add(x);
        }
        var chip = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x1A, 0x3A)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 4, 4),
            Child = sp, Cursor = Cursors.Hand
        };
        chip.MouseLeftButtonDown += (_, _) => { _activeTagFilter = tag; RefreshNotesTree(); };
        return chip;
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
            var chip = new Border
            {
                Background = _activeTagFilter == tag
                    ? new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED))
                    : new SolidColorBrush(Color.FromRgb(0x22, 0x16, 0x2E)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 6),
                Cursor = Cursors.Hand
            };
            var t = tag;
            chip.Child = new TextBlock { Text = t, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xAA, 0xFF)) };
            chip.MouseLeftButtonDown += (_, _) => { FilterByTag(t); RefreshAllTags(); };
            AllTagsPanel.Children.Add(chip);
        }
        if (allTags.Count == 0)
            AllTagsPanel.Children.Add(new TextBlock { Text = "No tags yet", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)) });
    }
}
