using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Security.Cryptography;
using SecureVault.Models;
using SecureVault.Crypto;

namespace SecureVault.Storage;
public static class VaultStore
{
    private const string VaultFile = "vault.dat";
    private static string VaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Bastion");
    public static string VaultPath => Path.Combine(VaultDirectory, VaultFile);

    public static bool Exists()
    {
        EnsureVaultLocation();
        return File.Exists(VaultPath);
    }

    public static void EnsureVaultLocation()
    {
        Directory.CreateDirectory(VaultDirectory);
        if (File.Exists(VaultPath)) return;

        foreach (var legacyPath in GetLegacyVaultPaths())
        {
            if (!File.Exists(legacyPath)) continue;
            var info = new FileInfo(legacyPath);
            if (info.Length < 44) continue;
            File.Copy(legacyPath, VaultPath, overwrite: false);
            return;
        }
    }

    private static IEnumerable<string> GetLegacyVaultPaths()
    {
        var paths = new[]
        {
            Path.Combine(Environment.CurrentDirectory, VaultFile),
            Path.Combine(AppContext.BaseDirectory, VaultFile)
        };

        return paths
            .Select(Path.GetFullPath)
            .Where(path => !string.Equals(path, Path.GetFullPath(VaultPath), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static void Save(Vault vault, string password)
        => SaveToFile(VaultPath, vault, password);

    public static void SaveToFile(string path, Vault vault, string password)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var salt = RandomNumberGenerator.GetBytes(16);
        var key = KeyDerivation.DeriveKey(password, salt);
        var json = JsonSerializer.Serialize(vault);
        var cipher = CryptoService.Encrypt(json, key, out var nonce, out var tag);
        using var fs = File.Create(path);
        fs.Write(salt); fs.Write(nonce); fs.Write(cipher); fs.Write(tag);
    }

    public static Vault Load(string password)
    {
        EnsureVaultLocation();
        return LoadFromFile(VaultPath, password);
    }

    public static Vault LoadFromFile(string path, string password)
    {
        var data = File.ReadAllBytes(path);
        var salt = data[..16];
        var nonce = data[16..28];
        var tag = data[^16..];
        var cipher = data[28..^16];
        var key = KeyDerivation.DeriveKey(password, salt);
        var json = CryptoService.Decrypt(cipher, key, nonce, tag);
        return JsonSerializer.Deserialize<Vault>(json)!;
    }
}
