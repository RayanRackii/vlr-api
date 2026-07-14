using Platform.Api.Modules.CustomerAuth.Dtos;

namespace Platform.Api.Modules.CustomerAuth.Services;

public interface ICustomerAuthService
{
    Task RequestOtpAsync(RequestOtpDto request, CancellationToken cancellationToken);

    Task<AuthResponseDto> VerifyOtpAsync(
        VerifyOtpDto request,
        CancellationToken cancellationToken);
}
