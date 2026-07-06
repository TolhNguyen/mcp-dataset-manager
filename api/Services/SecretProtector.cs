using System.Security.Cryptography;
using System.Text;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Encrypts small secrets (DB connection configs) at rest using AES-256-GCM.
/// Output format: "v1:" + base64(nonce[12] | ciphertext | tag[16]).
/// Master key comes from Encryption:MasterKey (env EDM_ENCRYPTION_KEY) and is
/// hashed with SHA-256 to derive the fixed-size AES key.
/// </summary>
public sealed class SecretProtector
{
    public const string VersionPrefix = "v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public SecretProtector(IConfiguration configuration)
    {
        var raw = configuration["Encryption:MasterKey"];
        if (string.IsNullOrWhiteSpace(raw) || raw.Length < 32)
        {
            throw new InvalidOperationException(
                "Encryption:MasterKey must be a random secret of at least 32 characters. " +
                "Set it via the EDM_ENCRYPTION_KEY environment variable.");
        }

        _key = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
    }

    public string Protect(string plaintext)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        var payload = new byte[NonceSize + cipher.Length + TagSize];
        nonce.CopyTo(payload, 0);
        cipher.CopyTo(payload, NonceSize);
        tag.CopyTo(payload, NonceSize + cipher.Length);

        return VersionPrefix + Convert.ToBase64String(payload);
    }

    public string Unprotect(string protectedValue)
    {
        if (!protectedValue.StartsWith(VersionPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Protected value has an unknown format version.");
        }

        var payload = Convert.FromBase64String(protectedValue[VersionPrefix.Length..]);
        if (payload.Length < NonceSize + TagSize)
        {
            throw new InvalidOperationException("Protected value is too short to be valid.");
        }

        var nonce = payload.AsSpan(0, NonceSize);
        var cipher = payload.AsSpan(NonceSize, payload.Length - NonceSize - TagSize);
        var tag = payload.AsSpan(payload.Length - TagSize, TagSize);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);

        return Encoding.UTF8.GetString(plain);
    }
}
