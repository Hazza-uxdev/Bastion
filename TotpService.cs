using System;
using System.Linq;
using System.Security.Cryptography;

namespace SecureVault;

public static class TotpService
{
    private const int PeriodSeconds = 30;
    private const int Digits = 6;

    public static string NormalizeSecret(string input)
    {
        input = (input ?? "").Trim();
        if (string.IsNullOrWhiteSpace(input)) return "";

        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, "otpauth", StringComparison.OrdinalIgnoreCase))
        {
            input = uri.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .FirstOrDefault(parts => string.Equals(Uri.UnescapeDataString(parts[0]), "secret", StringComparison.OrdinalIgnoreCase))?[1] ?? "";
            input = Uri.UnescapeDataString(input);
        }

        return new string(input
            .Where(c => !char.IsWhiteSpace(c) && c != '-')
            .Select(char.ToUpperInvariant)
            .ToArray());
    }

    public static string GenerateCode(string secret, DateTimeOffset? now = null)
    {
        secret = NormalizeSecret(secret);
        if (string.IsNullOrWhiteSpace(secret)) return "";

        var key = DecodeBase32(secret);
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var counter = timestamp.ToUnixTimeSeconds() / PeriodSeconds;
        Span<byte> counterBytes = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            counterBytes[i] = (byte)(counter & 0xff);
            counter >>= 8;
        }

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(counterBytes.ToArray());
        var offset = hash[^1] & 0x0f;
        var binary =
            ((hash[offset] & 0x7f) << 24) |
            ((hash[offset + 1] & 0xff) << 16) |
            ((hash[offset + 2] & 0xff) << 8) |
            (hash[offset + 3] & 0xff);
        var otp = binary % (int)Math.Pow(10, Digits);
        return otp.ToString(new string('0', Digits));
    }

    public static int SecondsRemaining(DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        return PeriodSeconds - (int)(timestamp.ToUnixTimeSeconds() % PeriodSeconds);
    }

    private static byte[] DecodeBase32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        value = NormalizeSecret(value).TrimEnd('=');
        var output = new System.Collections.Generic.List<byte>();
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var c in value)
        {
            var index = alphabet.IndexOf(c);
            if (index < 0) throw new FormatException("Invalid TOTP secret.");
            buffer = (buffer << 5) | index;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                output.Add((byte)((buffer >> (bitsLeft - 8)) & 0xff));
                bitsLeft -= 8;
            }
        }

        return output.ToArray();
    }
}
