using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Authentication;
using Platform.Api.Modules.CustomerAuth.Dtos;
using Platform.Api.Modules.RegistrationFields.Services;
using Platform.Api.Notifications;
using Platform.Api.Services.Brazil;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.CustomerAuth.Services;

public sealed class CustomerAuthService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    ICustomerJwtIssuer customerJwtIssuer,
    IViaCepClient viaCepClient,
    IRegistrationFieldService registrationFieldService,
    NotificationQueue notificationQueue,
    ILogger<CustomerAuthService> logger) : ICustomerAuthService
{
    private static readonly TimeSpan OtpLifetime = TimeSpan.FromMinutes(10);
    private static readonly PasswordHasher<Customer> PasswordHasher = new();
    private const int MinimumPasswordLength = 8;

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

        await IssueAndEnqueuePhoneCodeAsync(customer, cancellationToken);

        logger.LogInformation(
            "Legacy OTP generated for contact {Contact} (customer {CustomerId}).",
            contact.Normalized,
            customer.Id);
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

        await ConsumeOtpAsync(customer.Id, code, cancellationToken);

        if (contact.Kind == ContactKind.Phone && !customer.IsPhoneVerified)
        {
            customer.MarkPhoneVerified(DateTimeOffset.UtcNow);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return BuildAuthResponse(customer);
    }

    public async Task<RegisterCustomerResponseDto> RegisterAsync(
        RegisterCustomerRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();

        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length < 2)
        {
            throw new ArgumentException("Name is required.");
        }

        var email = NormalizeEmail(request.Email);
        var password = request.Password ?? string.Empty;
        if (password.Length < MinimumPasswordLength)
        {
            throw new ArgumentException(
                $"Password must be at least {MinimumPasswordLength} characters.");
        }

        var phone = BrazilianDocumentValidator.NormalizePhoneBr(request.Phone);

        var schema = await registrationFieldService.ListForTenantAsync(tenantId, cancellationToken);
        var extras = RegistrationAttributeValidator.ValidateAndNormalize(
            schema,
            request.Attributes);

        if (extras.TryGetValue("cpf", out var cpfValue) && !string.IsNullOrWhiteSpace(cpfValue))
        {
            cpfValue = BrazilianDocumentValidator.NormalizeCpf(cpfValue);
            extras["cpf"] = cpfValue;

            var cpfTaken = await dbContext.Customers
                .AnyAsync(c => c.Cpf == cpfValue, cancellationToken);
            if (cpfTaken)
            {
                throw new InvalidOperationException("A customer with this CPF already exists.");
            }
        }

        string? postalCode = null;
        string? street = null;
        string? neighborhood = null;
        string? city = null;
        string? state = null;

        var cepKey = extras.Keys.FirstOrDefault(k =>
            string.Equals(k, "cep", StringComparison.OrdinalIgnoreCase)
            || string.Equals(k, "postalCode", StringComparison.OrdinalIgnoreCase));

        if (cepKey is not null && !string.IsNullOrWhiteSpace(extras[cepKey]))
        {
            var address = await viaCepClient.LookupAsync(extras[cepKey]!, cancellationToken);
            postalCode = address.PostalCode;
            street = address.Street;
            neighborhood = address.Neighborhood;
            city = address.City;
            state = address.State;
            extras[cepKey] = postalCode;
        }

        string? photoUrl = null;
        if (extras.TryGetValue("photo", out var photo)
            || extras.TryGetValue("photoUrl", out photo))
        {
            photoUrl = photo;
        }

        var emailTaken = await dbContext.Customers
            .AnyAsync(c => c.Email == email, cancellationToken);
        if (emailTaken)
        {
            throw new InvalidOperationException("A customer with this email already exists.");
        }

        var phoneTaken = await dbContext.Customers
            .AnyAsync(c => c.Phone == phone, cancellationToken);
        if (phoneTaken)
        {
            throw new InvalidOperationException("A customer with this phone already exists.");
        }

        var customer = new Customer
        {
            TenantId = tenantId,
            Name = name,
            Email = email,
            Phone = phone,
            Cpf = extras.GetValueOrDefault("cpf"),
            PostalCode = postalCode,
            AddressStreet = street,
            AddressNeighborhood = neighborhood,
            AddressCity = city,
            AddressState = state,
            PhotoUrl = photoUrl,
            ExtraAttributes = extras,
        };

        customer.PasswordHash = PasswordHasher.HashPassword(customer, password);
        dbContext.Customers.Add(customer);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new InvalidOperationException(
                "A customer with the same email, CPF, or phone already exists.");
        }

        await IssueAndEnqueuePhoneCodeAsync(customer, cancellationToken);

        return new RegisterCustomerResponseDto(customer.Id, RequiresPhoneVerification: true);
    }

    public async Task<AuthResponseDto> VerifyPhoneAsync(
        VerifyPhoneRequestDto request,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var email = NormalizeEmail(request.Email);
        var code = (request.Code ?? string.Empty).Trim();

        if (!Regex.IsMatch(code, @"^\d{6}$"))
        {
            throw new UnauthorizedAccessException("Invalid or expired verification code.");
        }

        var customer = await dbContext.Customers
            .FirstOrDefaultAsync(c => c.Email == email, cancellationToken)
            ?? throw new UnauthorizedAccessException("Invalid or expired verification code.");

        await ConsumeOtpAsync(customer.Id, code, cancellationToken);

        customer.MarkPhoneVerified(DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);

        return BuildAuthResponse(customer);
    }

    public async Task<AuthResponseDto> LoginAsync(
        CustomerLoginRequestDto request,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var email = NormalizeEmail(request.Email);
        var password = request.Password ?? string.Empty;

        var customer = await dbContext.Customers
            .FirstOrDefaultAsync(c => c.Email == email, cancellationToken);

        if (customer is null
            || string.IsNullOrWhiteSpace(customer.PasswordHash)
            || PasswordHasher.VerifyHashedPassword(
                customer,
                customer.PasswordHash,
                password) == PasswordVerificationResult.Failed)
        {
            throw new UnauthorizedAccessException("Invalid email or password.");
        }

        if (!customer.IsPhoneVerified)
        {
            throw new UnauthorizedAccessException(
                "Phone number is not verified. Complete SMS verification first.");
        }

        customer.LastLoginAt = DateTimeOffset.UtcNow;
        customer.Touch();
        await dbContext.SaveChangesAsync(cancellationToken);

        return BuildAuthResponse(customer);
    }

    public async Task<TenantBrandingResponseDto> GetBrandingAsync(
        string subdomain,
        CancellationToken cancellationToken)
    {
        var normalized = subdomain.Trim().ToLowerInvariant();

        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(
                t => t.Subdomain != null
                     && t.Subdomain.ToLower() == normalized
                     && t.IsActive,
                cancellationToken)
            ?? throw new KeyNotFoundException(
                $"No active tenant found for subdomain '{normalized}'.");

        var displayName = string.IsNullOrWhiteSpace(tenant.TradeName)
            ? tenant.LegalName
            : tenant.TradeName!;

        return new TenantBrandingResponseDto(
            tenant.Subdomain!,
            displayName,
            tenant.LogoSvg,
            tenant.PrimaryColor,
            tenant.AccentColor,
            tenant.WelcomeTagline);
    }

    public async Task<CustomerProfileDto> GetCurrentAsync(
        Guid customerId,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var customer = await dbContext.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken)
            ?? throw new KeyNotFoundException("Customer not found.");

        return MapProfile(customer);
    }

    public async Task<CustomerProfileDto> UpdateProfileAsync(
        Guid customerId,
        UpdateCustomerProfileRequestDto request,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var customer = await dbContext.Customers
            .FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken)
            ?? throw new KeyNotFoundException("Customer not found.");

        string? name = null;
        string? photoUrl = null;
        var clearPhoto = false;

        if (request.NameSpecified)
        {
            name = (request.Name ?? string.Empty).Trim();
            if (name.Length is < 2 or > 200)
            {
                throw new ArgumentException("Name must be between 2 and 200 characters.");
            }
        }

        if (request.PhotoUrlSpecified)
        {
            if (request.PhotoUrl is null)
            {
                clearPhoto = true;
            }
            else
            {
                photoUrl = RegistrationAttributeValidator.NormalizePhoto(request.PhotoUrl);
            }
        }

        customer.UpdateProfile(name, photoUrl, clearPhoto);
        await dbContext.SaveChangesAsync(cancellationToken);

        return MapProfile(customer);
    }

    private async Task IssueAndEnqueuePhoneCodeAsync(
        Customer customer,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(customer.Phone))
        {
            throw new ArgumentException("Phone is required for SMS verification.");
        }

        var tenantId = customer.TenantId;
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

        await notificationQueue.EnqueueAsync(
            new NotificationMessage(
                Type: "Sms",
                Recipient: customer.Phone,
                Subject: "Verification",
                Body: $"Seu codigo Rolvix: {code}. Valido por 10 minutos."),
            cancellationToken);
    }

    private async Task ConsumeOtpAsync(
        Guid customerId,
        string code,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var otp = await dbContext.OtpCodes
            .Where(o => o.CustomerId == customerId
                        && !o.IsUsed
                        && o.ExpiresAt > now)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (otp is null || !string.Equals(otp.Code, code, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Invalid or expired verification code.");
        }

        otp.MarkAsUsed();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private AuthResponseDto BuildAuthResponse(Customer customer)
    {
        var token = customerJwtIssuer.IssueToken(customer);

        return new AuthResponseDto(
            token,
            new CustomerAuthProfileDto(
                customer.Id,
                customer.TenantId,
                customer.Name,
                customer.Phone,
                customer.Email,
                customer.CreatedAt,
                customer.IsPhoneVerified,
                customer.PhotoUrl,
                customer.ExtraAttributes));
    }

    private static CustomerProfileDto MapProfile(Customer customer) =>
        new(
            customer.Id,
            customer.TenantId,
            customer.Name,
            customer.Email,
            customer.Phone,
            customer.Cpf,
            customer.PostalCode,
            customer.AddressStreet,
            customer.AddressNeighborhood,
            customer.AddressCity,
            customer.AddressState,
            customer.PhotoUrl,
            customer.CreatedAt,
            customer.IsPhoneVerified,
            customer.ExtraAttributes);

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

    private static string NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("Email is required.");
        }

        var normalized = email.Trim().ToLowerInvariant();
        if (!normalized.Contains('@') || normalized.Length < 5)
        {
            throw new ArgumentException("Email format is invalid.");
        }

        return normalized;
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
            return new ParsedContact(ContactKind.Email, NormalizeEmail(trimmed));
        }

        return new ParsedContact(
            ContactKind.Phone,
            BrazilianDocumentValidator.NormalizePhoneBr(trimmed));
    }

    private enum ContactKind
    {
        Phone,
        Email
    }

    private sealed record ParsedContact(ContactKind Kind, string Normalized);
}
