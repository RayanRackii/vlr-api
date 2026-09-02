using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;
using Platform.Api.Authentication;

namespace Platform.Api.Tests.Authentication;

public sealed class JwtBearerSchemeSelectorTests
{
    [Fact]
    public void Missing_authorization_header_selects_supabase()
    {
        Assert.Equal(
            SupabaseJwtBearerDefaults.AuthenticationScheme,
            JwtBearerSchemeSelector.Select(new DefaultHttpContext()));
    }

    [Fact]
    public void Customer_issuer_selects_customer_jwt()
    {
        var context = BearerContext(Mint(issuer: CustomerJwtIssuer.Issuer));

        Assert.Equal(
            CustomerJwtBearerDefaults.AuthenticationScheme,
            JwtBearerSchemeSelector.Select(context));
    }

    [Fact]
    public void Other_issuer_selects_supabase()
    {
        var context = BearerContext(Mint(issuer: "https://example.supabase.co/auth/v1"));

        Assert.Equal(
            SupabaseJwtBearerDefaults.AuthenticationScheme,
            JwtBearerSchemeSelector.Select(context));
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("a.b")]
    [InlineData("Bearer")]
    [InlineData("Basic abc")]
    public void Malformed_or_non_bearer_selects_supabase_without_throwing(string header)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = header;

        var scheme = JwtBearerSchemeSelector.Select(context);

        Assert.Equal(SupabaseJwtBearerDefaults.AuthenticationScheme, scheme);
    }

    private static DefaultHttpContext BearerContext(string token)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer " + token;
        return context;
    }

    private static string Mint(string issuer)
    {
        var now = DateTime.UtcNow;
        var signingKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes("selector-test-secret-key-32bytes!!"));
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: CustomerJwtIssuer.Audience,
            claims: [new Claim("sub", Guid.NewGuid().ToString())],
            notBefore: now,
            expires: now.AddHours(1),
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
