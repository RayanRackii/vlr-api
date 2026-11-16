using Microsoft.Extensions.Options;
using Platform.Api.Modules.Payments;
using Platform.Api.Modules.Payments.Security;

namespace Platform.Api.Tests.Payments;

public sealed class PaymentSecretProtectorTests
{
    private const string Secret = "asaas-subaccount-key-plain-value";
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Round_trip_decrypts_and_ciphertext_is_not_the_plaintext()
    {
        var sut = Create(("v1", NewKey()));

        var protectedValue = sut.Protect(Secret, TenantA);

        Assert.DoesNotContain(Secret, protectedValue);
        Assert.Matches(@"^v1\.[^.]+\.[^.]*\.[^.]+$", protectedValue);
        Assert.Equal(Secret, sut.Unprotect(protectedValue, TenantA));
    }

    [Fact]
    public void Each_encryption_uses_a_fresh_nonce()
    {
        var sut = Create(("v1", NewKey()));

        Assert.NotEqual(sut.Protect(Secret, TenantA), sut.Protect(Secret, TenantA));
    }

    [Fact]
    public void Another_tenant_id_fails_to_decrypt()
    {
        var sut = Create(("v1", NewKey()));
        var protectedValue = sut.Protect(Secret, TenantA);

        var ex = Assert.Throws<PaymentConfigurationException>(() => sut.Unprotect(protectedValue, TenantB));
        Assert.DoesNotContain(Secret, ex.ToString());
    }

    [Fact]
    public void Tampered_ciphertext_fails_to_decrypt()
    {
        var sut = Create(("v1", NewKey()));
        var parts = sut.Protect(Secret, TenantA).Split('.');
        var bytes = Convert.FromBase64String(parts[2]);
        bytes[0] ^= 0xFF;
        parts[2] = Convert.ToBase64String(bytes);

        Assert.Throws<PaymentConfigurationException>(() => sut.Unprotect(string.Join('.', parts), TenantA));
    }

    [Fact]
    public void Old_version_still_decrypts_after_v2_is_added_and_new_writes_use_v2()
    {
        var v1 = NewKey();
        var old = Create(("v1", v1)).Protect(Secret, TenantA);

        var rotated = Create(("v1", v1), ("v2", NewKey()));

        Assert.Equal(Secret, rotated.Unprotect(old, TenantA));
        Assert.StartsWith("v2.", rotated.Protect(Secret, TenantA), StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_keys_throw_at_use_not_at_construction()
    {
        var sut = Create();

        Assert.Throws<PaymentConfigurationException>(() => sut.Protect(Secret, TenantA));
    }

    [Fact]
    public void Unknown_version_throws_configuration_exception()
    {
        var protectedValue = Create(("v1", NewKey())).Protect(Secret, TenantA);
        var onlyV2 = Create(("v2", NewKey()));

        Assert.Throws<PaymentConfigurationException>(() => onlyV2.Unprotect(protectedValue, TenantA));
    }

    [Theory]
    [InlineData("not-base64!!")]
    [InlineData("AAAA")]
    public void Invalid_key_material_throws_without_echoing_the_value(string badKey)
    {
        var sut = Create(("v1", badKey));

        var ex = Assert.Throws<PaymentConfigurationException>(() => sut.Protect(Secret, TenantA));
        Assert.DoesNotContain(badKey, ex.Message);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("v1.a.b")]
    [InlineData("v1.!!.!!.!!")]
    public void Malformed_stored_value_throws_configuration_exception(string stored)
    {
        var sut = Create(("v1", NewKey()));

        Assert.Throws<PaymentConfigurationException>(() => sut.Unprotect(stored, TenantA));
    }

    private static string NewKey() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    private static PaymentSecretProtector Create(params (string Version, string Key)[] keys)
    {
        var options = new PaymentsOptions();
        foreach (var (version, key) in keys)
        {
            options.EncryptionKeys[version] = key;
        }

        return new PaymentSecretProtector(new StaticMonitor(options));
    }

    private sealed class StaticMonitor(PaymentsOptions value) : IOptionsMonitor<PaymentsOptions>
    {
        public PaymentsOptions CurrentValue => value;

        public PaymentsOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<PaymentsOptions, string?> listener) => null;
    }
}
