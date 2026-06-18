using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SecureVault.Models
{
    public class Vault
    {
        public ObservableCollection<VaultEntry> Entries { get; set; } = new();
        public ObservableCollection<SecureNote> Notes { get; set; } = new();
        public List<string> Folders { get; set; } = new();
        public List<string> Tags { get; set; } = new();
        public List<VaultEntry> Trash { get; set; } = new();
        public List<SecureNote> NoteTrash { get; set; } = new();
        public VaultSettings Settings { get; set; } = new();
    }

    public class VaultSettings
    {
        public bool AutofillEnabled { get; set; } = true;
        public bool DarkTheme { get; set; } = true;
        public int LockMinutes { get; set; } = 3;
        public int ClipboardTimeoutSeconds { get; set; } = 15;
        public bool RunInTray { get; set; } = false;
        public bool StartOnBoot { get; set; } = false;
        public bool StartHiddenToTray { get; set; } = false;
        public string AccentColor { get; set; } = "#7C3AED";
        public int GeneratorLength { get; set; } = 18;
        public bool GeneratorUppercase { get; set; } = true;
        public bool GeneratorLowercase { get; set; } = true;
        public bool GeneratorDigits { get; set; } = true;
        public bool GeneratorSymbols { get; set; } = true;
        public string GraphNodeColor { get; set; } = "#FFEE00AA";
        public string GraphHubColor { get; set; } = "#FFFF00CC";
        public string GraphLineColor { get; set; } = "#8C00C8DC";
        public Dictionary<string, string> TagColors { get; set; } = new();
    }
}
