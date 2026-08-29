using Platform.Api.Services.Brazil;

namespace Platform.Api.Tests.Brazil;

public sealed class BrazilianDocumentValidatorTests
{
    [Theory]
    [InlineData("529.982.247-25", "52998224725")]
    [InlineData("39053344705", "39053344705")]
    public void NormalizeCpf_accepts_check_digits(string raw, string digits)
    {
        Assert.Equal(digits, BrazilianDocumentValidator.NormalizeCpf(raw));
    }

    [Theory]
    [InlineData("11111111111")]
    [InlineData("12345678901")]
    [InlineData("52998224724")]
    public void NormalizeCpf_rejects_invalid_check_digits(string raw)
    {
        Assert.Throws<ArgumentException>(() => BrazilianDocumentValidator.NormalizeCpf(raw));
    }

    [Theory]
    [InlineData("11.222.333/0001-81", "11222333000181")]
    [InlineData("04252011000110", "04252011000110")]
    public void NormalizeCnpj_accepts_check_digits(string raw, string digits)
    {
        Assert.Equal(digits, BrazilianDocumentValidator.NormalizeCnpj(raw));
    }

    [Theory]
    [InlineData("00000000000000")]
    [InlineData("11222333000180")]
    [InlineData("123")]
    public void NormalizeCnpj_rejects_invalid(string raw)
    {
        Assert.Throws<ArgumentException>(() => BrazilianDocumentValidator.NormalizeCnpj(raw));
    }
}
