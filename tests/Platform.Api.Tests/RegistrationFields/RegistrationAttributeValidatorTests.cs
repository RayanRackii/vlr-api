using System.Text.Json;
using Platform.Api.Modules.RegistrationFields.Dtos;
using Platform.Api.Modules.RegistrationFields.Services;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Tests.RegistrationFields;

public sealed class RegistrationAttributeValidatorTests
{
    [Fact]
    public void NormalizeCpf_rejects_eleven_digits_without_check_digits()
    {
        var schema = new List<RegistrationFieldDto>
        {
            Field("cpf", RegistrationFieldTypes.Cpf),
        };
        var attributes = new Dictionary<string, JsonElement>
        {
            ["cpf"] = JsonSerializer.SerializeToElement("11111111111"),
        };

        var ex = Assert.Throws<ArgumentException>(
            () => RegistrationAttributeValidator.ValidateAndNormalize(schema, attributes));
        Assert.Contains("CPF", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeCpf_accepts_valid_check_digits()
    {
        var schema = new List<RegistrationFieldDto>
        {
            Field("cpf", RegistrationFieldTypes.Cpf),
        };
        var attributes = new Dictionary<string, JsonElement>
        {
            ["cpf"] = JsonSerializer.SerializeToElement("529.982.247-25"),
        };

        var result = RegistrationAttributeValidator.ValidateAndNormalize(schema, attributes);
        Assert.Equal("52998224725", result["cpf"]);
    }

    [Fact]
    public void NormalizeCnpj_accepts_valid_check_digits()
    {
        var schema = new List<RegistrationFieldDto>
        {
            Field("cnpj", RegistrationFieldTypes.Cnpj),
        };
        var attributes = new Dictionary<string, JsonElement>
        {
            ["cnpj"] = JsonSerializer.SerializeToElement("11.222.333/0001-81"),
        };

        var result = RegistrationAttributeValidator.ValidateAndNormalize(schema, attributes);
        Assert.Equal("11222333000181", result["cnpj"]);
    }

    private static RegistrationFieldDto Field(string key, string type) =>
        new(Guid.NewGuid(), key, key.ToUpperInvariant(), type, IsRequired: true, SortOrder: 0, Options: null);
}
