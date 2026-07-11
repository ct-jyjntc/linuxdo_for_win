using System.Security.Cryptography;
using System.Text;

namespace LinuxDo.Core.Utilities;

/// <summary>RSA helpers for Discourse User API Key authorization flow.</summary>
public static class RSAHelper
{
    public sealed class KeyPair
    {
        public required RSA PrivateKey { get; init; }
        public required string PublicKeyPem { get; init; }
        public required byte[] PrivateKeyPkcs1 { get; init; }
    }

    public static KeyPair GenerateKeyPair(int bits = 2048)
    {
        var rsa = RSA.Create(bits);
        var privatePkcs1 = rsa.ExportRSAPrivateKey();
        var publicPkcs1 = rsa.ExportRSAPublicKey();
        var spki = WrapPkcs1PublicKeyAsSpki(publicPkcs1);
        var pem = PemEncode(spki, "PUBLIC KEY");
        return new KeyPair
        {
            PrivateKey = rsa,
            PublicKeyPem = pem,
            PrivateKeyPkcs1 = privatePkcs1
        };
    }

    public static RSA ImportPrivateKey(byte[] pkcs1)
    {
        var rsa = RSA.Create();
        rsa.ImportRSAPrivateKey(pkcs1, out _);
        return rsa;
    }

    /// <summary>Discourse encrypts the payload with the app public key using PKCS1 v1.5.</summary>
    public static byte[] DecryptPkcs1(string cipherBase64, RSA privateKey)
    {
        var cleaned = cipherBase64
            .Replace("\n", "", StringComparison.Ordinal)
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal)
            .Replace("-", "+", StringComparison.Ordinal)
            .Replace("_", "/", StringComparison.Ordinal);

        byte[] cipherData;
        try
        {
            cipherData = Convert.FromBase64String(cleaned);
        }
        catch (FormatException)
        {
            var pad = ((cleaned.Length + 3) / 4) * 4;
            var padded = cleaned.PadRight(pad, '=');
            cipherData = Convert.FromBase64String(padded);
        }

        return privateKey.Decrypt(cipherData, RSAEncryptionPadding.Pkcs1);
    }

    /// <summary>Wrap raw PKCS#1 RSA public key bytes into X.509 SubjectPublicKeyInfo.</summary>
    private static byte[] WrapPkcs1PublicKeyAsSpki(byte[] pkcs1)
    {
        // AlgorithmIdentifier for rsaEncryption: 30 0D 06 09 2A 86 48 86 F7 0D 01 01 01 05 00
        byte[] algorithmIdentifier =
        [
            0x30, 0x0D, 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x01, 0x05, 0x00
        ];
        var bitStringPayload = new byte[1 + pkcs1.Length];
        bitStringPayload[0] = 0x00;
        Buffer.BlockCopy(pkcs1, 0, bitStringPayload, 1, pkcs1.Length);
        var bitString = Asn1LengthPrefixed(0x03, bitStringPayload);
        var sequenceContent = new byte[algorithmIdentifier.Length + bitString.Length];
        Buffer.BlockCopy(algorithmIdentifier, 0, sequenceContent, 0, algorithmIdentifier.Length);
        Buffer.BlockCopy(bitString, 0, sequenceContent, algorithmIdentifier.Length, bitString.Length);
        return Asn1LengthPrefixed(0x30, sequenceContent);
    }

    private static byte[] Asn1LengthPrefixed(byte tag, byte[] content)
    {
        var length = content.Length;
        byte[] lenBytes;
        if (length < 0x80)
            lenBytes = [(byte)length];
        else if (length <= 0xFF)
            lenBytes = [0x81, (byte)length];
        else if (length <= 0xFFFF)
            lenBytes = [0x82, (byte)((length >> 8) & 0xFF), (byte)(length & 0xFF)];
        else
            lenBytes =
            [
                0x83,
                (byte)((length >> 16) & 0xFF),
                (byte)((length >> 8) & 0xFF),
                (byte)(length & 0xFF)
            ];

        var result = new byte[1 + lenBytes.Length + content.Length];
        result[0] = tag;
        Buffer.BlockCopy(lenBytes, 0, result, 1, lenBytes.Length);
        Buffer.BlockCopy(content, 0, result, 1 + lenBytes.Length, content.Length);
        return result;
    }

    private static string PemEncode(byte[] der, string label)
    {
        var b64 = Convert.ToBase64String(der);
        var sb = new StringBuilder();
        sb.Append("-----BEGIN ").Append(label).Append("-----\n");
        for (var i = 0; i < b64.Length; i += 64)
        {
            var len = Math.Min(64, b64.Length - i);
            sb.Append(b64, i, len).Append('\n');
        }
        sb.Append("-----END ").Append(label).Append("-----");
        return sb.ToString();
    }
}
