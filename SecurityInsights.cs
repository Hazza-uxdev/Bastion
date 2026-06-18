using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SecureVault.Models;

namespace SecureVault
{
    public static class SecurityInsights
    {
        private static readonly string[] CommonPasswordFragments =
        {
            "password", "passw0rd", "admin", "qwerty", "letmein", "welcome",
            "login", "monkey", "dragon", "football", "baseball", "iloveyou",
            "abc123", "111111", "123123", "1234", "bastion"
        };

        public static PasswordHealthResult AnalyzePassword(string pw)
        {
            if (string.IsNullOrEmpty(pw))
                return new PasswordHealthResult(0, "Weak", "Empty password", new[] { "Empty password" });

            var issues = new List<string>();
            var length = pw.Length;
            var hasLower = Regex.IsMatch(pw, "[a-z]");
            var hasUpper = Regex.IsMatch(pw, "[A-Z]");
            var hasDigit = Regex.IsMatch(pw, "[0-9]");
            var hasSymbol = Regex.IsMatch(pw, "[^a-zA-Z0-9]");

            var charsetSize = 0;
            if (hasLower) charsetSize += 26;
            if (hasUpper) charsetSize += 26;
            if (hasDigit) charsetSize += 10;
            if (hasSymbol) charsetSize += 33;
            charsetSize = Math.Max(charsetSize, 1);

            var entropyBits = length * Math.Log(charsetSize, 2);
            var entropyScore = (int)Math.Clamp((entropyBits - 28) / 72 * 55, 0, 55);
            var lengthScore = Math.Min(length * 2, 30);
            var varietyScore = new[] { hasLower, hasUpper, hasDigit, hasSymbol }.Count(v => v) * 5;
            var score = entropyScore + lengthScore + varietyScore;

            if (length < 12) { score -= 18; issues.Add("Use at least 12 characters"); }
            else if (length < 16) { score -= 6; issues.Add("16+ characters is safer"); }

            if (!hasLower || !hasUpper || !hasDigit || !hasSymbol)
                issues.Add("Mix uppercase, lowercase, numbers, and symbols");

            if (Regex.IsMatch(pw, @"(.)\1{2,}"))
            {
                score -= 12;
                issues.Add("Avoid repeated characters");
            }

            if (HasKeyboardOrAlphabetSequence(pw))
            {
                score -= 14;
                issues.Add("Avoid obvious sequences");
            }

            if (CommonPasswordFragments.Any(fragment => pw.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            {
                score -= 22;
                issues.Add("Avoid common password words");
            }

            if (Regex.IsMatch(pw, @"(19|20)\d{2}"))
            {
                score -= 6;
                issues.Add("Avoid years and dates");
            }

            score = Math.Clamp(score, 0, 100);
            return new PasswordHealthResult(
                score,
                StrengthLabel(score),
                issues.Count == 0 ? "Good length, variety, and entropy" : string.Join("; ", issues.Take(2)),
                issues);
        }

        public static int PasswordStrength(string pw) => AnalyzePassword(pw).Score;

        public static string StrengthLabel(int score) => score switch
        {
            < 35 => "Weak",
            < 60 => "Fair",
            < 78 => "Good",
            < 92 => "Strong",
            _    => "Very Strong"
        };

        public static List<VaultEntry> FindWeak(IEnumerable<VaultEntry> entries)
            => entries.Where(e => !e.IsDeleted && PasswordStrength(e.Password) < 70).ToList();

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

        private static bool HasKeyboardOrAlphabetSequence(string value)
        {
            var lower = value.ToLowerInvariant();
            string[] sequences =
            {
                "abcdefghijklmnopqrstuvwxyz",
                "zyxwvutsrqponmlkjihgfedcba",
                "01234567890",
                "09876543210",
                "qwertyuiop",
                "poiuytrewq",
                "asdfghjkl",
                "lkjhgfdsa",
                "zxcvbnm",
                "mnbvcxz"
            };

            for (var length = 4; length <= Math.Min(6, lower.Length); length++)
            {
                for (var i = 0; i <= lower.Length - length; i++)
                {
                    var part = lower.Substring(i, length);
                    if (sequences.Any(seq => seq.Contains(part)))
                        return true;
                }
            }

            return false;
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

    public sealed class PasswordHealthResult
    {
        public PasswordHealthResult(int score, string label, string summary, IEnumerable<string> issues)
        {
            Score = score;
            Label = label;
            Summary = summary;
            Issues = issues.ToList();
        }

        public int Score { get; }
        public string Label { get; }
        public string Summary { get; }
        public IReadOnlyList<string> Issues { get; }
        public string Display => $"{Label} ({Score}%)";
    }
}
