using System;
using System.Collections.Generic;

namespace SecureVault.Models
{
    public class VaultEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string Url { get; set; } = "";
        public List<string> Tags { get; set; } = new();
        public bool IsPinned { get; set; } = false;
        public bool IsDeleted { get; set; } = false;
        public string TotpSecret { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public string TagsText => string.Join(", ", Tags);
        public bool HasTotp => !string.IsNullOrWhiteSpace(TotpSecret);
        public string TotpCode => HasTotp ? TotpService.GenerateCode(TotpSecret) : "";
        public string TotpDisplay => HasTotp ? $"{TotpCode} ({TotpService.SecondsRemaining()}s)" : "";
        public string MaskedPassword => new string('●', Password?.Length ?? 0);
    }
}
