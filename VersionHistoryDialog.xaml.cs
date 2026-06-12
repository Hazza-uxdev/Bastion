using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using SecureVault.Models;

namespace SecureVault
{
    public class SnapshotViewModel
    {
        public DateTime SavedAt { get; set; }
        public string Body { get; set; } = "";
        public string Title { get; set; } = "";
        public string WordCount => Body.Split(new[]{' ','\n','\r','\t'}, StringSplitOptions.RemoveEmptyEntries).Length + " words";
    }

    public partial class VersionHistoryDialog : Window
    {
        public string? RestoredBody { get; private set; }
        public string? RestoredTitle { get; private set; }

        public VersionHistoryDialog(SecureNote note)
        {
            InitializeComponent();
            var snapshots = note.History
                .OrderByDescending(h => h.SavedAt)
                .Select(h => new SnapshotViewModel { SavedAt = h.SavedAt, Body = SnapshotBody(h), Title = h.Title })
                .ToList();
            VersionList.ItemsSource = snapshots;
            if (snapshots.Count > 0) VersionList.SelectedIndex = 0;
        }

        private void VersionList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (VersionList.SelectedItem is SnapshotViewModel vm)
                PreviewBox.Text = $"# {vm.Title}\n\n{vm.Body}";
        }

        private void Restore_Click(object sender, RoutedEventArgs e)
        {
            if (VersionList.SelectedItem is SnapshotViewModel vm)
            { RestoredBody = vm.Body; RestoredTitle = vm.Title; DialogResult = true; }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        { if (e.ChangedButton == MouseButton.Left) DragMove(); }

        private static string SnapshotBody(NoteSnapshot snapshot)
        {
            if (!string.IsNullOrEmpty(snapshot.Body)) return snapshot.Body;
            if (string.IsNullOrEmpty(snapshot.CompressedBodyBase64)) return "";
            try
            {
                var bytes = Convert.FromBase64String(snapshot.CompressedBodyBase64);
                using var input = new MemoryStream(bytes);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                gzip.CopyTo(output);
                return Encoding.UTF8.GetString(output.ToArray());
            }
            catch { return ""; }
        }
    }
}
