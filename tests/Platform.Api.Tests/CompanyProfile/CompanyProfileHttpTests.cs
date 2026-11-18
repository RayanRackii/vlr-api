using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Modules.Company.Controllers;
using Platform.Api.Modules.Company.Dtos;
using Platform.Api.Modules.Company.Services;
using Platform.Api.Services.Trial;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.CompanyProfile;

public sealed class CompanyProfileHttpTests
{
    private const string ValidCpf = "52998224725";
    private const string ValidCnpj = "11222333000181";
    private const string OtherCnpj = "11444777000161";

    private static object ValidBody(string taxId = ValidCnpj) => new
    {
        legalName = "  Acme Servicos Ltda ",
        tradeName = "Acme",
        taxId,
        contactEmail = "contato@acme.com.br",
        contactPhone = "(11) 99999-8888",
        address = new
        {
            postalCode = "01310-100",
            street = "Avenida Paulista",
            number = "1000",
            complement = "Sala 1",
            district = "Bela Vista",
            city = "Sao Paulo",
            state = "sp",
        },
    };

    [Fact]
    public async Task Get_returns_only_the_current_tenant_profile_without_any_write_permission()
    {
        await using var harness = new Harness();
        using var host = StartHost(harness);
        var client = host.GetTestClient();

        var profile = await client.GetFromJsonAsync<CompanyProfileResponse>("/api/company");

        Assert.NotNull(profile);
        Assert.Equal("First Co", profile.LegalName);
        Assert.Equal(ValidCnpj, profile.TaxId);
        Assert.Equal("first", profile.Subdomain);
        Assert.Null(profile.ContactEmail);
        Assert.Null(profile.Address.City);
    }

    [Fact]
    public async Task Get_without_tenant_context_is_unauthorized()
    {
        await using var harness = new Harness();
        harness.Provider.TenantId = null;
        using var host = StartHost(harness);

        var response = await host.GetTestClient().GetAsync("/api/company");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Put_requires_company_write_permission()
    {
        await using var harness = new Harness();
        using var host = StartHost(harness);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthentication.Header, Permissions.Core.UnitsWrite);

        var response = await client.PutAsJsonAsync("/api/company", ValidBody());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("First Co", (await harness.Db.Tenants.SingleAsync(t => t.Id == harness.FirstTenant)).LegalName);
    }

    [Fact]
    public async Task Put_normalizes_persists_and_leaves_other_tenants_and_platform_fields_untouched()
    {
        await using var harness = new Harness();
        using var host = StartHost(harness);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthentication.Header, Permissions.Core.CompanyWrite);

        var response = await client.PutAsJsonAsync("/api/company", ValidBody("11.222.333/0001-81"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<CompanyProfileResponse>();
        Assert.NotNull(dto);
        Assert.Equal("Acme Servicos Ltda", dto.LegalName);
        Assert.Equal("Acme", dto.TradeName);
        Assert.Equal(ValidCnpj, dto.TaxId);
        Assert.Equal("contato@acme.com.br", dto.ContactEmail);
        Assert.Equal("+5511999998888", dto.ContactPhone);
        Assert.Equal("01310100", dto.Address.PostalCode);
        Assert.Equal("SP", dto.Address.State);
        Assert.Equal("Sao Paulo", dto.Address.City);
        Assert.Equal("first", dto.Subdomain);

        harness.Db.ChangeTracker.Clear();
        var second = await harness.Db.Tenants.SingleAsync(t => t.Id == harness.SecondTenant);
        Assert.Equal("Second Co", second.LegalName);
        Assert.Equal(OtherCnpj, second.TaxId);
        Assert.Null(second.AddressCity);
        var first = await harness.Db.Tenants.SingleAsync(t => t.Id == harness.FirstTenant);
        Assert.Equal("Sao Paulo", first.AddressCity);
        Assert.Equal("first", first.Subdomain);
        Assert.True(first.IsActive);
    }

    [Fact]
    public async Task Put_accepts_a_valid_cpf_and_clears_optional_fields()
    {
        await using var harness = new Harness();
        using var host = StartHost(harness);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthentication.Header, Permissions.Core.CompanyWrite);

        var response = await client.PutAsJsonAsync("/api/company", new { legalName = "Maria Silva", taxId = "529.982.247-25" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<CompanyProfileResponse>();
        Assert.NotNull(dto);
        Assert.Equal(ValidCpf, dto.TaxId);
        Assert.Null(dto.TradeName);
        Assert.Null(dto.ContactPhone);
        Assert.Null(dto.Address.PostalCode);
    }

    [Theory]
    [InlineData("legalName", "   ")]
    [InlineData("taxId", "11111111111")]
    [InlineData("taxId", "11222333000182")]
    [InlineData("taxId", "123")]
    [InlineData("taxId", "")]
    [InlineData("contactEmail", "not-an-email")]
    [InlineData("contactEmail", "a@b")]
    [InlineData("contactPhone", "123")]
    [InlineData("postalCode", "1234")]
    [InlineData("state", "SPA")]
    [InlineData("state", "S1")]
    public async Task Put_rejects_invalid_values(string field, string value)
    {
        await using var harness = new Harness();
        using var host = StartHost(harness);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthentication.Header, Permissions.Core.CompanyWrite);

        var body = new Dictionary<string, object?>
        {
            ["legalName"] = "Acme Ltda",
            ["taxId"] = ValidCnpj,
            ["contactEmail"] = "contato@acme.com.br",
            ["contactPhone"] = "(11) 99999-8888",
        };
        var address = new Dictionary<string, object?> { ["postalCode"] = "01310100", ["state"] = "SP", ["city"] = "Sao Paulo" };
        if (field is "postalCode" or "state")
        {
            address[field] = value;
        }
        else
        {
            body[field] = value;
        }

        body["address"] = address;

        var response = await client.PutAsJsonAsync("/api/company", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        harness.Db.ChangeTracker.Clear();
        Assert.Equal("First Co", (await harness.Db.Tenants.SingleAsync(t => t.Id == harness.FirstTenant)).LegalName);
    }

    [Fact]
    public async Task Put_rejects_a_tax_id_that_belongs_to_another_company()
    {
        await using var harness = new Harness();
        using var host = StartHost(harness);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthentication.Header, Permissions.Core.CompanyWrite);

        var response = await client.PutAsJsonAsync("/api/company", ValidBody(OtherCnpj));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Put_is_blocked_while_the_trial_is_read_only()
    {
        await using var harness = new Harness();
        var first = await harness.Db.Tenants.SingleAsync(t => t.Id == harness.FirstTenant);
        first.ConfigureAsTrial(DateTimeOffset.UtcNow.AddDays(-20));
        await harness.Db.SaveChangesAsync();
        using var host = StartHost(harness, realTrialGuard: true);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthentication.Header, Permissions.Core.CompanyWrite);

        var response = await client.PutAsJsonAsync("/api/company", ValidBody());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        harness.Db.ChangeTracker.Clear();
        Assert.Equal("First Co", (await harness.Db.Tenants.SingleAsync(t => t.Id == harness.FirstTenant)).LegalName);

        // Reads stay allowed on a read-only trial.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/company")).StatusCode);
    }

    private static IHost StartHost(Harness harness, bool realTrialGuard = false) => new HostBuilder().ConfigureWebHost(web =>
    {
        web.UseTestServer();
        web.ConfigureServices(services =>
        {
            services.AddSingleton(harness.Db);
            services.AddSingleton<ITenantProvider>(harness.Provider);
            if (realTrialGuard)
            {
                services.AddSingleton<IPlatformAdminChecker>(new FakePlatformAdminChecker());
                services.AddSingleton<ITrialGuard, TrialGuard>();
            }
            else
            {
                services.AddSingleton<ITrialGuard, FakeTrialGuard>();
            }

            services.AddSingleton<ICompanyProfileService, CompanyProfileService>();
            services.AddSingleton<IAuthorizationHandler, HeaderPermissionHandler>();
            services.AddAuthentication(SupabaseJwtBearerDefaults.AuthenticationScheme)
                .AddScheme<AuthenticationSchemeOptions, TestAuthentication>(SupabaseJwtBearerDefaults.AuthenticationScheme, _ => { });
            services.AddAuthorization(options => options.AddRolvixPolicies());
            services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
            services.AddControllers().ConfigureApplicationPartManager(manager =>
            {
                manager.ApplicationParts.Clear();
                manager.ApplicationParts.Add(new AssemblyPart(typeof(CompanyController).Assembly));
                foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToArray()) manager.FeatureProviders.Remove(provider);
                manager.FeatureProviders.Add(new CompanyControllerFeatureProvider());
            });
        });
        web.Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); });
    }).Start();

    private sealed class Harness : IAsyncDisposable
    {
        public Guid FirstTenant { get; }
        public Guid SecondTenant { get; }
        public FakeTenantProvider Provider { get; } = new();
        public AppDbContext Db { get; }

        public Harness()
        {
            Db = InMemoryAppDb.Create(Provider);
            var first = new Tenant("First Co", ValidCnpj, "First", "first");
            var second = new Tenant("Second Co", OtherCnpj, "Second", "second");
            Db.Tenants.AddRange(first, second);
            Db.SaveChanges();
            FirstTenant = first.Id;
            SecondTenant = second.Id;
            Provider.TenantId = FirstTenant;
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class HeaderPermissionHandler : AuthorizationHandler<PermissionRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
        {
            if (context.User.HasClaim("permission", requirement.PermissionKey)) context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Header = "X-Permission";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = Request.Headers[Header].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(value => new Claim("permission", value));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private sealed class CompanyControllerFeatureProvider : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) => typeInfo.AsType() == typeof(CompanyController);
    }
}
