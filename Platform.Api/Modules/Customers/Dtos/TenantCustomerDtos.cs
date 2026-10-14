namespace Platform.Api.Modules.Customers.Dtos;

public sealed record TenantCustomerListItemDto(
    Guid Id,
    string Name,
    string? Email,
    bool EmailVerified,
    DateTimeOffset CreatedAt);
