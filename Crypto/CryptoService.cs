using System.Security.Cryptography;
using System.Text;
namespace SecureVault.Crypto;
public static class CryptoService
{
    public static byte[] Encrypt(string plaintext, byte[] key, out byte[] nonce, out byte[] tag)
    {
        nonce = RandomNumberGenerator.GetBytes(12);
        tag = new byte[16];
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return cipher;
    }

    public static string Decrypt(byte[] cipher, byte[] key, byte[] nonce, byte[] tag)
    {
        var data = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, data);
        return Encoding.UTF8.GetString(data);
    }
}