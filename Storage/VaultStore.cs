using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using SecureVault.Models;
using SecureVault.Crypto;

namespace SecureVault.Storage;
public static class VaultStore
{
    private const string VaultFile = "vault.dat";

    public static void Save(Vault vault, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = KeyDerivation.DeriveKey(password, salt);
        var json = JsonSerializer.Serialize(vault);
        var cipher = CryptoService.Encrypt(json, key, out var nonce, out var tag);
        using var fs = File.Create(VaultFile);
        fs.Write(salt); fs.Write(nonce); fs.Write(cipher); fs.Write(tag);
    }

    public static Vault Load(string password)
    {
        var data = File.ReadAllBytes(VaultFile);
        var salt = data[..16];
        var nonce = data[16..28];
        var tag = data[^16..];
        var cipher = data[28..^16];
        var key = KeyDerivation.DeriveKey(password, salt);
        var json = CryptoService.Decrypt(cipher, key, nonce, tag);
        return JsonSerializer.Deserialize<Vault>(json)!;
    }
}