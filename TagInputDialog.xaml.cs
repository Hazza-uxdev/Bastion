using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace SecureVault
{
    public partial class TagInputDialog : Window
    {
        public string TagName => TagBox.Text.Trim().ToLower();

        public TagInputDialog(List<string> existingTags)
        {
            InitializeComponent();
            ExistingTagsList.ItemsSource = existingTags;
            Loaded += (_, _) => TagBox.Focus();
        }

        private void Add_Click(object sender, RoutedEventArgs e) => DialogResult = true;
        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
        private void TagBox_KeyDown(object sender, KeyEventArgs e)
        { if (e.Key == Key.Enter) DialogResult = true; if (e.Key == Key.Escape) DialogResult = false; }
        private void ExistingTag_Selected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        { if (ExistingTagsList.SelectedItem is string t) { TagBox.Text = t; DialogResult = true; } }
    }
}
