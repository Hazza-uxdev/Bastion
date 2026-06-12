using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using SecureVault.Models;

namespace SecureVault
{
    public static class SecurityInsights
    {
        public static int PasswordStrength(string pw)
        {
            if (string.IsNullOrEmpty(pw)) return 0;
            int score = 0;
            if (pw.Length >= 8)  score += 20;
            if (pw.Length >= 12) score += 10;
            if (pw.Length >= 16) score += 10;
            if (System.Text.RegularExpressions.Regex.IsMatch(pw, "[a-z]")) score += 15;
            if (System.Text.RegularExpressions.Regex.IsMatch(pw, "[A-Z]")) score += 15;
            if (System.Text.RegularExpressions.Regex.IsMatch(pw, "[0-9]")) score += 15;
            if (System.Text.RegularExpressions.Regex.IsMatch(pw, "[^a-zA-Z0-9]")) score += 15;
            return Math.Min(score, 100);
        }

        public static string StrengthLabel(int score) => score switch
        {
            < 30 => "Weak",
            < 55 => "Fair",
            < 75 => "Good",
            < 90 => "Strong",
            _    => "Very Strong"
        };

        public static List<VaultEntry> FindWeak(IEnumerable<VaultEntry> entries)
            => entries.Where(e => !e.IsDeleted && PasswordStrength(e.Password) < 55).ToList();

        public static List<VaultEntry> FindReused(IEnumerable<VaultEntry> entries)
        {
            var active = entries.Where(e => !e.IsDeleted).ToList();
            var groups = active.GroupBy(e => e.Password).Where(g => g.Count() > 1);
            return groups.SelectMany(g => g).ToList();
        }

        public static List<VaultEntry> FindDuplicates(IEnumerable<VaultEntry> entries)
        {
            var active = entries.Where(e => !e.IsDeleted).ToList();
            return active.GroupBy(e => e.Title.ToLower())
                         .Where(g => g.Count() > 1)
                         .SelectMany(g => g).ToList();
        }

        // k-Anonymity model: only first 5 chars of SHA1 sent
        private static readonly HttpClient _http = new();
        public static async Task<int> CheckBreachAsync(string password)
        {
            try
            {
                using var sha1 = SHA1.Create();
                var hash = Convert.ToHexString(sha1.ComputeHash(Encoding.UTF8.GetBytes(password)));
                var prefix = hash[..5]; var suffix = hash[5..];
                var resp = await _http.GetStringAsync($"https://api.pwnedpasswords.com/range/{prefix}");
                foreach (var line in resp.Split('\n'))
                {
                    var parts = line.Split(':');
                    if (parts[0].Trim().Equals(suffix, StringComparison.OrdinalIgnoreCase))
                        return int.Parse(parts[1].Trim());
                }
                return 0;
            }
            catch { return -1; } // -1 = check failed
        }
    }
}
