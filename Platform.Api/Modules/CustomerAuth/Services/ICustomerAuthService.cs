using Platform.Api.Modules.CustomerAuth.Dtos;

namespace Platform.Api.Modules.CustomerAuth.Services;

public interface ICustomerAuthService
{
    Task RequestOtpAsync(RequestOtpDto request, CancellationToken cancellationToken);

    Task<AuthResponseDto> VerifyOtpAsync(
        VerifyOtpDto request,
        CancellationToken cancellationToken);

    Task<RegisterCustomerResponseDto> RegisterAsync(
        RegisterCustomerRequestDto request,
        CancellationToken cancellationToken);

    Task<AuthResponseDto> VerifyPhoneAsync(
        VerifyPhoneRequestDto request,
        CancellationToken cancellationToken);

    Task<AuthResponseDto> LoginAsync(
        CustomerLoginRequestDto request,
        CancellationToken cancellationToken);

    Task<TenantBrandingResponseDto> GetBrandingAsync(
        string subdomain,
        CancellationToken cancellationToken);

    Task<CustomerProfileDto> GetCurrentAsync(
        Guid customerId,
        CancellationToken cancellationToken);

    Task<CustomerProfileDto> UpdateProfileAsync(
        Guid customerId,
        UpdateCustomerProfileRequestDto request,
        CancellationToken cancellationToken);
}
