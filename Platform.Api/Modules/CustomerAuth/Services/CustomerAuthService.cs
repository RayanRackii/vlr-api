using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Authentication;
using Platform.Api.Modules.CustomerAuth.Dtos;
using Platform.Api.Modules.CustomerAuth.PhoneVerification;
using Platform.Api.Modules.RegistrationFields.Services;
using Platform.Api.Services.Brazil;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.CustomerAuth.Services;

public sealed class CustomerAuthService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    ICustomerJwtIssuer customerJwtIssuer,
    IViaCepClient viaCepClient,
    IRegistrationFieldService registrationFieldService,
    IPhoneVerificationClient phoneVerification,
    IPhoneVerificationSendGate sendGate,
    ILogger<CustomerAuthService> logger,
    IHttpContextAccessor? httpContextAccessor = null) : ICustomerAuthService
{
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

        var phone = contact.Kind == ContactKind.Phone
            ? contact.Normalized
            : customer?.Phone;
        if (string.IsNullOrWhiteSpace(phone))
        {
            throw new ArgumentException("Phone is required for SMS verification.");
        }

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
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await phoneVerification.StartVerificationAsync(phone, cancellationToken);

        logger.LogInformation(
            "Phone verification started for customer {CustomerId}.",
            customer.Id);
    }

    public async Task<AuthResponseDto> VerifyOtpAsync(
        VerifyOtpDto request,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var contact = ParseContact(request.Contact);
        var code = RequireSixDigitCode(request.Code);

        var customer = await FindCustomerByContactAsync(contact, cancellationToken)
            ?? throw new UnauthorizedAccessException("Invalid or expired verification code.");

        var phone = contact.Kind == ContactKind.Phone
            ? contact.Normalized
            : customer.Phone;
        if (string.IsNullOrWhiteSpace(phone))
        {
            throw new ArgumentException("Phone is required for SMS verification.");
        }

        await phoneVerification.CheckVerificationAsync(phone, code, cancellationToken);

        if (!customer.IsPhoneVerified)
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

        var document = NormalizeCustomerDocument(request.CustomerType, request.Document);
        string? cpf = request.CustomerType == CustomerType.Individual ? document : null;

        var schema = await registrationFieldService.ListForTenantAsync(tenantId, cancellationToken);
        var extras = RegistrationAttributeValidator.ValidateAndNormalize(
            schema,
            request.Attributes);

        if (cpf is not null)
        {
            extras["cpf"] = cpf;
        }

        var matches = await LoadRegistrationMatchesAsync(
            email,
            phone,
            document,
            cpf,
            cancellationToken);

        if (matches.Exists(c => c.PhoneVerifiedAt is not null))
        {
            throw DuplicateIdentityException(
                matches.Where(c => c.PhoneVerifiedAt is not null).ToList(),
                email,
                phone,
                document,
                cpf);
        }

        Customer customer;
        if (matches.Count == 0)
        {
            customer = await InsertCustomerAsync(
                tenantId,
                name,
                email,
                password,
                phone,
                request.CustomerType,
                cpf,
                document,
                extras,
                cancellationToken);
        }
        else if (matches.Count == 1
                 && IsFullPendingMatch(matches[0], email, phone, document))
        {
            customer = matches[0];
            customer.Name = name;
            customer.PasswordHash = PasswordHasher.HashPassword(customer, password);
            customer.Touch();
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        else
        {
            throw DuplicateIdentityException(matches, email, phone, document, cpf);
        }

        var started = await TryStartVerificationForRegisterAsync(
            tenantId,
            email,
            RequirePhone(customer),
            cancellationToken);

        return new RegisterCustomerResponseDto(
            customer.Id,
            RequiresPhoneVerification: true,
            VerificationStarted: started);
    }

    public async Task<AuthResponseDto> VerifyPhoneAsync(
        VerifyPhoneRequestDto request,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var email = NormalizeEmail(request.Email);
        var code = RequireSixDigitCode(request.Code);

        var customer = await dbContext.Customers
            .FirstOrDefaultAsync(c => c.Email == email, cancellationToken)
            ?? throw new UnauthorizedAccessException("Invalid or expired verification code.");

        var phone = RequirePhone(customer);
        await phoneVerification.CheckVerificationAsync(phone, code, cancellationToken);

        customer.MarkPhoneVerified(DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);

        return BuildAuthResponse(customer);
    }

    public async Task ResendVerificationAsync(
        ResendVerificationRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();

        var email = NormalizeEmail(request.Email);
        var clientIp = GetClientIp();
        var decision = sendGate.Decide(tenantId, email, clientIp);

        if (decision == PhoneVerificationSendDecision.Limited)
        {
            throw new PhoneVerificationRateLimitedException(
                TwilioVerifyPhoneVerificationClient.RateLimitedMessage);
        }

        var customer = await dbContext.Customers
            .FirstOrDefaultAsync(c => c.Email == email, cancellationToken);

        if (customer is null
            || customer.IsPhoneVerified
            || string.IsNullOrWhiteSpace(customer.Phone))
        {
            return;
        }

        if (decision == PhoneVerificationSendDecision.Cooldown)
        {
            return;
        }

        try
        {
            await phoneVerification.StartVerificationAsync(customer.Phone, cancellationToken);
            sendGate.RecordSuccess(tenantId, email, clientIp);
        }
        catch (PhoneVerificationProviderException)
        {
        }
        catch (PhoneVerificationRateLimitedException)
        {
        }
        catch (PhoneVerificationInvalidException)
        {
        }
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
                customer.CustomerType,
                customer.Document,
                customer.Cpf,
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
            customer.CustomerType,
            customer.Document,
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

    private async Task<List<Customer>> LoadRegistrationMatchesAsync(
        string email,
        string phone,
        string document,
        string? cpf,
        CancellationToken cancellationToken)
    {
        return await dbContext.Customers
            .Where(c =>
                c.Email == email
                || c.Phone == phone
                || c.Document == document
                || (cpf != null && c.Cpf == cpf))
            .ToListAsync(cancellationToken);
    }

    private async Task<Customer> InsertCustomerAsync(
        Guid tenantId,
        string name,
        string email,
        string password,
        string phone,
        CustomerType customerType,
        string? cpf,
        string document,
        Dictionary<string, string?> extras,
        CancellationToken cancellationToken)
    {
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

        var customer = new Customer
        {
            TenantId = tenantId,
            Name = name,
            Email = email,
            Phone = phone,
            CustomerType = customerType,
            Cpf = cpf,
            Document = document,
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
                "A customer with the same email, document, or phone already exists.");
        }

        return customer;
    }

    private async Task<bool> TryStartVerificationForRegisterAsync(
        Guid tenantId,
        string email,
        string phone,
        CancellationToken cancellationToken)
    {
        var clientIp = GetClientIp();
        var decision = sendGate.Decide(tenantId, email, clientIp);
        if (decision == PhoneVerificationSendDecision.Cooldown)
        {
            return true;
        }

        if (decision == PhoneVerificationSendDecision.Limited)
        {
            return false;
        }

        try
        {
            await phoneVerification.StartVerificationAsync(phone, cancellationToken);
            sendGate.RecordSuccess(tenantId, email, clientIp);
            return true;
        }
        catch (PhoneVerificationProviderException)
        {
            return false;
        }
        catch (PhoneVerificationRateLimitedException)
        {
            return false;
        }
        catch (PhoneVerificationInvalidException)
        {
            return false;
        }
    }

    private string? GetClientIp()
    {
        var httpContext = httpContextAccessor?.HttpContext;
        if (httpContext is null)
        {
            return null;
        }

        var forwarded = httpContext.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var firstHop = forwarded.Split(',', 2)[0].Trim();
            if (firstHop.Length > 0)
            {
                return firstHop;
            }
        }

        return httpContext.Connection.RemoteIpAddress?.ToString();
    }

    private static bool IsFullPendingMatch(
        Customer customer,
        string email,
        string phone,
        string document) =>
        string.Equals(customer.Email, email, StringComparison.Ordinal)
        && string.Equals(customer.Phone, phone, StringComparison.Ordinal)
        && string.Equals(customer.Document, document, StringComparison.Ordinal);

    private static InvalidOperationException DuplicateIdentityException(
        IReadOnlyList<Customer> matches,
        string email,
        string phone,
        string document,
        string? cpf)
    {
        if (matches.Any(c => c.Document == document))
        {
            return new InvalidOperationException("A customer with this document already exists.");
        }

        if (cpf is not null && matches.Any(c => c.Cpf == cpf))
        {
            return new InvalidOperationException("A customer with this CPF already exists.");
        }

        if (matches.Any(c => c.Email == email))
        {
            return new InvalidOperationException("A customer with this email already exists.");
        }

        if (matches.Any(c => c.Phone == phone))
        {
            return new InvalidOperationException("A customer with this phone already exists.");
        }

        return new InvalidOperationException(
            "A customer with the same email, document, or phone already exists.");
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

    private static string RequirePhone(Customer customer)
    {
        if (string.IsNullOrWhiteSpace(customer.Phone))
        {
            throw new ArgumentException("Phone is required for SMS verification.");
        }

        return customer.Phone;
    }

    private static string RequireSixDigitCode(string? code)
    {
        var trimmed = (code ?? string.Empty).Trim();
        if (!Regex.IsMatch(trimmed, @"^\d{6}$"))
        {
            throw new PhoneVerificationInvalidException("Invalid or expired verification code.");
        }

        return trimmed;
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

    private static string NormalizeCustomerDocument(CustomerType customerType, string? raw)
    {
        return customerType switch
        {
            CustomerType.Individual => BrazilianDocumentValidator.NormalizeCpf(raw),
            CustomerType.Company => BrazilianDocumentValidator.NormalizeCnpj(raw),
            _ => throw new ArgumentException("Customer type is invalid."),
        };
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
