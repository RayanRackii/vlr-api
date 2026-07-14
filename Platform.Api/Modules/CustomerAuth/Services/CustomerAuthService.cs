using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Authentication;
using Platform.Api.Modules.CustomerAuth.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.CustomerAuth.Services;

public sealed class CustomerAuthService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    ICustomerJwtIssuer customerJwtIssuer,
    ILogger<CustomerAuthService> logger) : ICustomerAuthService
{
    private static readonly TimeSpan OtpLifetime = TimeSpan.FromMinutes(10);

    public async Task RequestOtpAsync(
        RequestOtpDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();

        var name = request.Name?.Trim()
            ?? throw new ArgumentException("Name is required.");

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.");
        }

        var contact = ParseContact(request.Contact);
        var customer = await FindCustomerByContactAsync(contact, cancellationToken);

        if (customer is null)
        {
            customer = new Customer
            {
                TenantId = tenantId,
                Name = name,
                Phone = contact.Kind == ContactKind.Phone ? contact.Normalized : null,
                Email = contact.Kind == ContactKind.Email ? contact.Normalized : null,
            };

            dbContext.Customers.Add(customer);
        }
        else if (!string.Equals(customer.Name, name, StringComparison.Ordinal))
        {
            customer.Name = name;
            customer.Touch();
        }

        var code = GenerateOtpCode();
        var now = DateTimeOffset.UtcNow;

        var previousOtps = await dbContext.OtpCodes
            .Where(o => o.CustomerId == customer.Id && !o.IsUsed)
            .ToListAsync(cancellationToken);

        foreach (var previous in previousOtps)
        {
            previous.MarkAsUsed();
        }

        dbContext.OtpCodes.Add(new OtpCode
        {
            TenantId = tenantId,
            CustomerId = customer.Id,
            Code = code,
            ExpiresAt = now.Add(OtpLifetime),
            IsUsed = false,
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Código gerado para {Contact}: {Code}",
            contact.Normalized,
            code);
    }

    public async Task<AuthResponseDto> VerifyOtpAsync(
        VerifyOtpDto request,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var contact = ParseContact(request.Contact);
        var code = (request.Code ?? string.Empty).Trim();

        if (!Regex.IsMatch(code, @"^\d{6}$"))
        {
            throw new UnauthorizedAccessException("Invalid or expired OTP code.");
        }

        var customer = await FindCustomerByContactAsync(contact, cancellationToken)
            ?? throw new UnauthorizedAccessException("Invalid or expired OTP code.");

        var now = DateTimeOffset.UtcNow;

        var otp = await dbContext.OtpCodes
            .Where(o => o.CustomerId == customer.Id
                        && !o.IsUsed
                        && o.ExpiresAt > now)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (otp is null || !string.Equals(otp.Code, code, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Invalid or expired OTP code.");
        }

        otp.MarkAsUsed();
        await dbContext.SaveChangesAsync(cancellationToken);

        var token = customerJwtIssuer.IssueToken(customer);

        return new AuthResponseDto(
            token,
            new CustomerAuthProfileDto(
                customer.Id,
                customer.TenantId,
                customer.Name,
                customer.Phone,
                customer.Email,
                customer.CreatedAt));
    }

    private async Task<Customer?> FindCustomerByContactAsync(
        ParsedContact contact,
        CancellationToken cancellationToken)
    {
        return contact.Kind switch
        {
            ContactKind.Email => await dbContext.Customers
                .FirstOrDefaultAsync(c => c.Email == contact.Normalized, cancellationToken),
            ContactKind.Phone => await dbContext.Customers
                .FirstOrDefaultAsync(c => c.Phone == contact.Normalized, cancellationToken),
            _ => null
        };
    }

    private Guid EnsureTenantContext()
    {
        return tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");
    }

    private static string GenerateOtpCode()
    {
        return RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    }

    private static ParsedContact ParseContact(string? rawContact)
    {
        if (string.IsNullOrWhiteSpace(rawContact))
        {
            throw new ArgumentException("Contact is required.");
        }

        var trimmed = rawContact.Trim();

        if (trimmed.Contains('@', StringComparison.Ordinal))
        {
            var email = trimmed.ToLowerInvariant();

            if (!email.Contains('.') || email.Length < 5)
            {
                throw new ArgumentException("Contact email format is invalid.");
            }

            return new ParsedContact(ContactKind.Email, email);
        }

        var phoneDigits = Regex.Replace(trimmed, @"[^\d+]", string.Empty);

        if (phoneDigits.Length < 8)
        {
            throw new ArgumentException("Contact phone format is invalid.");
        }

        return new ParsedContact(ContactKind.Phone, phoneDigits);
    }

    private enum ContactKind
    {
        Phone,
        Email
    }

    private sealed record ParsedContact(ContactKind Kind, string Normalized);
}
