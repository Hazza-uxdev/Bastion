using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace SecureVault
{
    public class CommandItem
    {
        public string Icon { get; set; } = "";
        public string Label { get; set; } = "";
        public string Sub { get; set; } = "";
        public string Shortcut { get; set; } = "";
        public Action? Execute { get; set; }
    }

    public partial class CommandPalette : Window
    {
        private readonly List<CommandItem> _allCommands;
        public CommandItem? Selected { get; private set; }

        public CommandPalette(List<CommandItem> commands)
        {
            InitializeComponent();
            _allCommands = commands;
            ResultsList.ItemsSource = _allCommands;
            if (_allCommands.Count > 0) ResultsList.SelectedIndex = 0;
            Loaded += (_, _) => SearchInput.Focus();
            Deactivated += (_, _) => Close();
        }

        private void SearchInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            var q = SearchInput.Text.Trim().ToLower();
            ResultsList.ItemsSource = string.IsNullOrEmpty(q)
                ? _allCommands
                : (System.Collections.IEnumerable)_allCommands
                    .Where(c => c.Label.ToLower().Contains(q) || c.Sub.ToLower().Contains(q))
                    .ToList();
            if (ResultsList.Items.Count > 0) ResultsList.SelectedIndex = 0;
        }

        private void SearchInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down && ResultsList.Items.Count > 0)
            { ResultsList.SelectedIndex = Math.Min(ResultsList.SelectedIndex + 1, ResultsList.Items.Count - 1); e.Handled = true; }
            else if (e.Key == Key.Up && ResultsList.Items.Count > 0)
            { ResultsList.SelectedIndex = Math.Max(ResultsList.SelectedIndex - 1, 0); e.Handled = true; }
            else if (e.Key == Key.Enter) ExecuteSelected();
            else if (e.Key == Key.Escape) Close();
        }

        private void ResultsList_KeyDown(object sender, KeyEventArgs e)
        { if (e.Key == Key.Enter) ExecuteSelected(); }

        private void ResultsList_Execute(object sender, MouseButtonEventArgs e)
        { ExecuteSelected(); }

        private void ExecuteSelected()
        {
            if (ResultsList.SelectedItem is CommandItem cmd)
            { Selected = cmd; Close(); cmd.Execute?.Invoke(); }
        }
    }
}
