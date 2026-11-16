using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Platform.Api.Modules.Payments.Security;

/// <summary>Encrypts provider secrets (subaccount API keys) before they are persisted.</summary>
public interface IPaymentSecretProtector
{
    /// <summary>Returns <c>v{n}.{nonce}.{ciphertext}.{tag}</c> (base64 parts) bound to <paramref name="tenantId"/>.</summary>
    string Protect(string plaintext, Guid tenantId);

    string Unprotect(string protectedValue, Guid tenantId);
}

/// <summary>
/// AES-256-GCM with versioned keys from <c>Payments:EncryptionKeys:{version}</c>. The highest version encrypts, any listed
/// version decrypts. The tenant id is the associated data, so a ciphertext copied to another tenant's row does not decrypt.
/// Keys are read at use time: a missing key throws <see cref="PaymentConfigurationException"/> then, not at startup.
/// </summary>
public sealed class PaymentSecretProtector : IPaymentSecretProtector
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly IOptionsMonitor<PaymentsOptions> _options;

    public PaymentSecretProtector(IOptionsMonitor<PaymentsOptions> options)
    {
        _options = options;
    }

    public string Protect(string plaintext, Guid tenantId)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);

        var keys = LoadKeys();
        if (keys.Count == 0)
        {
            throw new PaymentConfigurationException("No payment encryption key is configured (Payments:EncryptionKeys).");
        }

        var version = keys.Keys.Max();
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(keys[version], TagSize))
        {
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, AssociatedData(tenantId));
        }

        CryptographicOperations.ZeroMemory(plaintextBytes);
        return string.Join(
            '.',
            "v" + version.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(ciphertext),
            Convert.ToBase64String(tag));
    }

    public string Unprotect(string protectedValue, Guid tenantId)
    {
        ArgumentException.ThrowIfNullOrEmpty(protectedValue);

        var parts = protectedValue.Split('.');
        if (parts.Length != 4 || !TryParseVersion(parts[0], out var version))
        {
            throw UnknownFormat();
        }

        var keys = LoadKeys();
        if (!keys.TryGetValue(version, out var key))
        {
            throw new PaymentConfigurationException(
                $"Payment encryption key v{version.ToString(CultureInfo.InvariantCulture)} is not configured.");
        }

        byte[] nonce, ciphertext, tag;
        try
        {
            nonce = Convert.FromBase64String(parts[1]);
            ciphertext = Convert.FromBase64String(parts[2]);
            tag = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            throw UnknownFormat();
        }

        if (nonce.Length != NonceSize || tag.Length != TagSize)
        {
            throw UnknownFormat();
        }

        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData(tenantId));
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException)
        {
            throw new PaymentConfigurationException("The stored payment secret could not be decrypted.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static PaymentConfigurationException UnknownFormat() =>
        new("The stored payment secret has an unknown format.");

    private static byte[] AssociatedData(Guid tenantId) =>
        Encoding.UTF8.GetBytes(tenantId.ToString("D", CultureInfo.InvariantCulture));

    private Dictionary<int, byte[]> LoadKeys()
    {
        var result = new Dictionary<int, byte[]>();
        foreach (var (name, value) in _options.CurrentValue.EncryptionKeys)
        {
            if (!TryParseVersion(name, out var version))
            {
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(value ?? string.Empty);
            }
            catch (FormatException)
            {
                throw InvalidKey(version);
            }

            if (bytes.Length != KeySize)
            {
                throw InvalidKey(version);
            }

            result[version] = bytes;
        }

        return result;
    }

    private static PaymentConfigurationException InvalidKey(int version) =>
        new($"Payment encryption key v{version.ToString(CultureInfo.InvariantCulture)} must be base64 of exactly 32 bytes.");

    private static bool TryParseVersion(string text, out int version)
    {
        version = 0;
        return text.Length > 1
            && (text[0] == 'v' || text[0] == 'V')
            && int.TryParse(text.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out version)
            && version > 0;
    }
}
