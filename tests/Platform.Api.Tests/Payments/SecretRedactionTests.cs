using Platform.Api.Modules.Payments.Gateways;
using Platform.Api.Modules.Payments.Gateways.Asaas;

namespace Platform.Api.Tests.Payments;

public sealed class SecretRedactionTests
{
    [Fact]
    public void SubaccountResult_ToString_hides_the_api_key()
    {
        var text = new SubaccountResult("acc_1", "wal_1", "secret-key", "https://onboard").ToString();

        Assert.DoesNotContain("secret-key", text);
        Assert.Contains("acc_1", text);
        Assert.Contains("***", text);
    }

    [Fact]
    public void AsaasAccountResponse_ToString_hides_the_api_key()
    {
        var text = new AsaasAccountResponse("acc_1", "secret-key", "wal_1", null).ToString();

        Assert.DoesNotContain("secret-key", text);
        Assert.Contains("***", text);
    }

    [Fact]
    public void AsaasWebhookBody_ToString_hides_the_auth_token()
    {
        var text = new AsaasWebhookBody("Rolvix", "https://x", true, false, 3, "secret-key", "NON_SEQUENTIALLY", ["PAYMENT_RECEIVED"])
            .ToString();

        Assert.DoesNotContain("secret-key", text);
        Assert.Contains("***", text);
    }
}
