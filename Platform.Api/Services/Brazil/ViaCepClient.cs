using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Platform.Api.Services.Brazil;

namespace Platform.Api.Services.Brazil;

public interface IViaCepClient
{
    Task<ViaCepAddress> LookupAsync(string postalCode, CancellationToken cancellationToken);
}

public sealed record ViaCepAddress(
    string PostalCode,
    string Street,
    string Neighborhood,
    string City,
    string State);

public sealed class ViaCepClient(HttpClient httpClient) : IViaCepClient
{
    public async Task<ViaCepAddress> LookupAsync(
        string postalCode,
        CancellationToken cancellationToken)
    {
        var cep = BrazilianDocumentValidator.NormalizePostalCode(postalCode);
        using var response = await httpClient.GetAsync($"ws/{cep}/json/", cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new ArgumentException("Could not validate CEP. Try again.");
        }

        var payload = await response.Content.ReadFromJsonAsync<ViaCepResponse>(
            cancellationToken: cancellationToken);

        if (payload is null || payload.Erro)
        {
            throw new ArgumentException("CEP was not found.");
        }

        return new ViaCepAddress(
            cep,
            payload.Logradouro?.Trim() ?? string.Empty,
            payload.Bairro?.Trim() ?? string.Empty,
            payload.Localidade?.Trim() ?? string.Empty,
            payload.Uf?.Trim().ToUpperInvariant() ?? string.Empty);
    }

    private sealed class ViaCepResponse
    {
        [JsonPropertyName("cep")]
        public string? Cep { get; init; }

        [JsonPropertyName("logradouro")]
        public string? Logradouro { get; init; }

        [JsonPropertyName("bairro")]
        public string? Bairro { get; init; }

        [JsonPropertyName("localidade")]
        public string? Localidade { get; init; }

        [JsonPropertyName("uf")]
        public string? Uf { get; init; }

        [JsonPropertyName("erro")]
        public bool Erro { get; init; }
    }
}
