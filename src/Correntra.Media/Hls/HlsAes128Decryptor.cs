using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Correntra.Media.Hls;

public static class HlsAes128Decryptor
{
    public static byte[] Decrypt(ReadOnlySpan<byte> encrypted, ReadOnlySpan<byte> key, ReadOnlySpan<byte> explicitIv, long sequence)
    {
        if (key.Length != 16)
        {
            throw new ArgumentException("HLS AES-128 keys must contain exactly 16 bytes.", nameof(key));
        }

        byte[] iv = explicitIv.Length == 0 ? DeriveIv(sequence) : explicitIv.ToArray();
        if (iv.Length != 16)
        {
            throw new ArgumentException("HLS AES-128 initialization vectors must contain exactly 16 bytes.", nameof(explicitIv));
        }

        using Aes aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key.ToArray();
        aes.IV = iv;
        using ICryptoTransform decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(encrypted.ToArray(), 0, encrypted.Length);
    }

    public static byte[] DeriveIv(long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);

        var iv = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(iv.AsSpan(8), sequence);
        return iv;
    }
}
