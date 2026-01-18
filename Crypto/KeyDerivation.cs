using System.Security.Cryptography;
namespace SecureVault.Crypto;
public static class KeyDerivation
{
    public static byte[] DeriveKey(string password, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 600_000, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(32);
    }
}