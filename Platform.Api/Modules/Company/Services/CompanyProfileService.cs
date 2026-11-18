using System.Net.Mail;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Api.Modules.Company.Dtos;
using Platform.Api.Services.Brazil;
using Platform.Api.Services.Trial;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Company.Services;

public interface ICompanyProfileService
{
    Task<CompanyProfileResponse> GetAsync(CancellationToken cancellationToken);

    Task<CompanyProfileResponse> UpdateAsync(
        UpdateCompanyProfileRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Company profile of the CURRENT tenant only (never addressed by id). Validation errors surface as
/// <see cref="ArgumentException"/> (400); a read-only trial or a duplicate document as
/// <see cref="InvalidOperationException"/> (409).
/// </summary>
public sealed class CompanyProfileService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    ITrialGuard trialGuard) : ICompanyProfileService
{
    public async Task<CompanyProfileResponse> GetAsync(CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var tenant = await dbContext.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Company not found.");
        return Map(tenant);
    }

    public async Task<CompanyProfileResponse> UpdateAsync(
        UpdateCompanyProfileRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        await trialGuard.EnsureWritableAsync(cancellationToken);

        var tenant = await dbContext.Tenants
            .FirstOrDefaultAsync(item => item.Id == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Company not found.");

        var address = request.Address;
        tenant.UpdateCompanyProfile(
            RequireText(request.LegalName, "Legal name"),
            request.TradeName,
            NormalizeTaxId(request.TaxId),
            NormalizeEmail(request.ContactEmail),
            string.IsNullOrWhiteSpace(request.ContactPhone)
                ? null
                : BrazilianDocumentValidator.NormalizePhoneBr(request.ContactPhone),
            string.IsNullOrWhiteSpace(address?.PostalCode)
                ? null
                : BrazilianDocumentValidator.NormalizePostalCode(address.PostalCode),
            address?.Street,
            address?.Number,
            address?.Complement,
            address?.District,
            address?.City,
            address?.State);

        if (await dbContext.Tenants.AnyAsync(
                item => item.TaxId == tenant.TaxId && item.Id != tenantId,
                cancellationToken))
        {
            throw new InvalidOperationException("This CPF/CNPJ is already registered.");
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        })
        {
            throw new InvalidOperationException("This CPF/CNPJ is already registered.");
        }

        return Map(tenant);
    }

    private Guid RequireTenant() =>
        tenantProvider.TenantId is Guid id && id != Guid.Empty
            ? id
            : throw new UnauthorizedAccessException("Tenant context is required.");

    private static string RequireText(string? value, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{field} is required.")
            : value.Trim();

    private static string NormalizeTaxId(string? raw)
    {
        var digits = Regex.Replace(raw ?? string.Empty, @"\D", string.Empty);
        return digits.Length switch
        {
            11 => BrazilianDocumentValidator.NormalizeCpf(digits),
            14 => BrazilianDocumentValidator.NormalizeCnpj(digits),
            _ => throw new ArgumentException("Tax id must be a valid CPF (11 digits) or CNPJ (14 digits)."),
        };
    }

    private static string? NormalizeEmail(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var email = raw.Trim();
        try
        {
            var parsed = new MailAddress(email);
            if (!string.Equals(parsed.Address, email, StringComparison.Ordinal) || !parsed.Host.Contains('.'))
            {
                throw new ArgumentException("E-mail is invalid.");
            }
        }
        catch (FormatException)
        {
            throw new ArgumentException("E-mail is invalid.");
        }

        return email;
    }

    private static CompanyProfileResponse Map(Tenant tenant) => new(
        tenant.LegalName,
        tenant.TradeName,
        tenant.TaxId,
        tenant.ContactEmail,
        tenant.ContactPhone,
        new CompanyAddressDto(
            tenant.AddressPostalCode,
            tenant.AddressStreet,
            tenant.AddressNumber,
            tenant.AddressComplement,
            tenant.AddressDistrict,
            tenant.AddressCity,
            tenant.AddressState),
        tenant.Subdomain);
}
