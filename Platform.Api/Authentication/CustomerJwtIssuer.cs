using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Platform.Core.Domain.Entities;

namespace Platform.Api.Authentication;

public interface ICustomerJwtIssuer
{
    string IssueToken(Customer customer);
}

public sealed class CustomerJwtIssuer(IConfiguration configuration) : ICustomerJwtIssuer
{
    public const string Issuer = "platform.b2c";

    public const string Audience = "platform.customer";

    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(30);

    public string IssueToken(Customer customer)
    {
        var jwtSecret = configuration["Supabase:JwtSecret"]
            ?? throw new InvalidOperationException("Supabase:JwtSecret is not configured.");

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var now = DateTime.UtcNow;

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, customer.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(CustomerClaimTypes.CustomerId, customer.Id.ToString()),
            new(CustomerClaimTypes.TenantId, customer.TenantId.ToString()),
            new(CustomerClaimTypes.Role, AuthRoles.Customer),
            new(ClaimTypes.Role, AuthRoles.Customer),
        };

        if (!string.IsNullOrWhiteSpace(customer.Email))
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Email, customer.Email));
        }

        if (!string.IsNullOrWhiteSpace(customer.Phone))
        {
            claims.Add(new Claim("phone", customer.Phone));
        }

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            notBefore: now,
            expires: now.Add(TokenLifetime),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
