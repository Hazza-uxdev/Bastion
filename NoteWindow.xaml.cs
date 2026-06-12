using System.Windows;
using SecureVault.Models;

namespace SecureVault
{
    // NoteWindow is no longer used — notes are edited inline in the Obsidian-style editor.
    public partial class NoteWindow : Window
    {
        public SecureNote Note { get; private set; } = new SecureNote();

        public NoteWindow(SecureNote? existing = null)
        {
            InitializeComponent();
        }

        private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
