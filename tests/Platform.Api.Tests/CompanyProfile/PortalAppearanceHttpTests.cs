using System.Net;
using System.Net.Http.Headers;
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
using Platform.Api.Services.Billing;
using Platform.Api.Storage;
using Platform.Api.Tests.CustomerAuth;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.CompanyProfile;

public sealed class PortalAppearanceHttpTests
{
    private const string FirstCnpj = "11222333000181";
    private const string SecondCnpj = "11444777000161";
    private const string Route = "/api/portal-appearance";
    private const string ImageRoute = "/api/portal-appearance/landing-image";

    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x01, 0x02, 0x03];

    private static readonly byte[] JpegBytes =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x02];

    private static readonly byte[] WebpBytes =
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 0x10, 0x00, 0x00, 0x00, (byte)'W', (byte)'E', (byte)'B', (byte)'P', 0x01];

    [Fact]
    public async Task Get_returns_empty_appearance_and_tagline_without_any_write_permission()
    {
        await using var harness = new Harness();
        var first = await harness.Db.Tenants.SingleAsync(t => t.Id == harness.FirstTenant);
        first.UpdateProfile("First Co", FirstCnpj, "First", "first", null, null, null, "Seja bem-vindo");
        await harness.Db.SaveChangesAsync();
        using var host = StartHost(harness);

        var dto = await host.GetTestClient().GetFromJsonAsync<PortalAppearanceResponse>(Route);

        Assert.NotNull(dto);
        Assert.Null(dto.LandingImageUrl);
        Assert.Null(dto.LandingKicker);
        Assert.Null(dto.LandingHeadline);
        Assert.Null(dto.LandingSubtitle);
        Assert.Equal("Seja bem-vindo", dto.WelcomeTagline);
    }

    [Fact]
    public async Task Get_without_tenant_context_is_unauthorized()
    {
        await using var harness = new Harness();
        harness.Provider.TenantId = null;
        using var host = StartHost(harness);

        var response = await host.GetTestClient().GetAsync(Route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_never_exposes_another_tenant_appearance()
    {
        await using var harness = new Harness();
        var second = await harness.Db.Tenants.SingleAsync(t => t.Id == harness.SecondTenant);
        second.UpdateLandingTexts("Outro", "Outro titulo", "Outro subtitulo");
        second.SetLandingImageKey("tenants/other/landing/x.png");
        await harness.Db.SaveChangesAsync();
        using var host = StartHost(harness);

        var dto = await host.GetTestClient().GetFromJsonAsync<PortalAppearanceResponse>(Route);

        Assert.NotNull(dto);
        Assert.Null(dto.LandingImageUrl);
        Assert.Null(dto.LandingKicker);
        Assert.Null(dto.LandingHeadline);
    }

    [Fact]
    public async Task Put_requires_company_write_permission()
    {
        await using var harness = new Harness();
        using var host = StartHost(harness);
        var client = WithPermission(host, Permissions.Core.UnitsWrite);

        var response = await client.PutAsJsonAsync(Route, new { landingKicker = "Quadras" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        harness.Db.ChangeTracker.Clear();
        Assert.Null((await harness.Db.Tenants.SingleAsync(t => t.Id == harness.FirstTenant)).LandingKicker);
    }

    [Fact]
    public async Task Put_trims_blanks_to_null_persists_and_leaves_other_tenants_untouched()
    {
        await using var harness = new Harness();
        using var host = StartHost(harness);
        var client = WithPermission(host, Permissions.Core.CompanyWrite);

        var response = await client.PutAsJsonAsync(Route, new
        {
            landingKicker = "  Reserve ja  ",
            landingHeadline = "Sua quadra, no seu tempo",
            landingSubtitle = "   ",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<PortalAppearanceResponse>();
        Assert.NotNull(dto);
        Assert.Equal("Reserve ja", dto.LandingKicker);
        Assert.Equal("Sua quadra, no seu tempo", dto.LandingHeadline);
        Assert.Null(dto.LandingSubtitle);

        harness.Db.ChangeTracker.Clear();
        var second = await harness.Db.Tenants.SingleAsync(t => t.Id == harness.SecondTenant);
        Assert.Null(second.LandingKicker);
        Assert.Null(second.LandingHeadline);

        var cleared = await client.PutAsJsonAsync(Route, new { });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var clearedDto = await cleared.Content.ReadFromJsonAsync<PortalAppearanceResponse>();
        Assert.NotNull(clearedDto);
        Assert.Null(clearedDto.LandingKicker);
        Assert.Null(clearedDto.LandingHeadline);
    }

    [Theory]
    [InlineData("landingKicker", 61)]
    [InlineData("landingHeadline", 121)]
    [InlineData("landingSubtitle", 241)]
    public async Task Put_rejects_values_over_the_length_limit(string field, int length)
    {
        await using var harness = new Harness();
        using var host = StartHost(harness);
        var client = WithPermission(host, Permissions.Core.CompanyWrite);

        var response = await client.PutAsJsonAsync(Route, new Dictionary<string, string> { [field] = new string('a', length) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        harness.Db.ChangeTracker.Clear();
        Assert.Null((await harness.Db.Tenants.SingleAsync(t => t.Id == harness.FirstTenant)).LandingKicker);
    }

    [Theory]
    [InlineData("landingKicker", 60)]
    [InlineData("landingHeadline", 120)]
    [InlineData("landingSubtitle", 240)]
    public async Task Put_accepts_values_at_the_length_limit(string field, int length)
    {
        await using var harness = new Harness();
        using var host = StartHost(harness);
        var client = WithPermission(host, Permissions.Core.CompanyWrite);

        var response = await client.PutAsJsonAsync(Route, new Dictionary<string, string> { [field] = new string('a', length) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Writes_are_blocked_while_the_trial_is_read_only_but_reads_stay_allowed()
    {
        await using var harness = new Harness();
        var first = await harness.Db.Tenants.SingleAsync(t => t.Id == harness.FirstTenant);
        first.ConfigureAsTrial(DateTimeOffset.UtcNow.AddDays(-(TrialLimits.TrialDays + 1)));
        await harness.Db.SaveChangesAsync();
        using var host = StartHost(harness, realPlanGuard: true);
        var client = WithPermission(host, Permissions.Core.CompanyWrite);

        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(Route, new { landingKicker = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(ImageRoute, ImageContent(PngBytes, "a.png", "image/png"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync(ImageRoute)).StatusCode);
        Assert.Empty(harness.Storage.Uploads);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Route)).StatusCode);
    }

    [Fact]
    public async Task Upload_requires_company_write_permission()
    {
        await using var harness = new Harness();
        using var host = StartHost(harness);
        var client = WithPermission(host, Permissions.Core.UnitsWrite);

        var response = await client.PostAsync(ImageRoute, ImageContent(PngBytes, "a.png", "image/png"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(harness.Storage.Uploads);
    }

    [Theory]
    [InlineData("a.png", "image/png", "png")]
    [InlineData("a.jpg", "image/jpeg", "jpg")]
    [InlineData("a.jpeg", "image/jpeg", "jpg")]
    [InlineData("a.webp", "image/webp", "webp")]
    public async Task Upload_stores_the_image_in_the_public_bucket_under_the_tenant_path(string fileName, string contentType, string kind)
    {
        await using var harness = new Harness();
        using var host = StartHost(harness);
        var client = WithPermission(host, Permissions.Core.CompanyWrite);
        var bytes = kind switch { "png" => PngBytes, "jpg" => JpegBytes, _ => WebpBytes };

        var response = await client.PostAsync(ImageRoute, ImageContent(bytes, fileName, contentType));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<PortalAppearanceResponse>();
        Assert.NotNull(dto);
        var upload = Assert.Single(harness.Storage.Uploads);
        Assert.Equal(new StorageOptions().PublicBucket, upload.Bucket);
        Assert.StartsWith($"tenants/{harness.FirstTenant:N}/landing/", upload.Key, StringComparison.Ordinal);
        Assert.EndsWith("." + kind, upload.Key, StringComparison.Ordinal);
        Assert.Equal($"https://storage.test/{upload.Bucket}/{upload.Key}", dto.LandingImageUrl);

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(upload.Key, (await harness.Db.Tenants.SingleAsync(t => t.Id == harness.FirstTenant)).LandingImageKey);
        Assert.Null((await harness.Db.Tenants.SingleAsync(t => t.Id == harness.SecondTenant)).LandingImageKey);
    }

    [Theory]
    [InlineData("a.gif", "image/gif", "FILE_TYPE_NOT_ALLOWED")]
    [InlineData("a.txt", "text/plain", "FILE_TYPE_NOT_ALLOWED")]
    [InlineData("a.svg", "image/svg+xml", "FILE_TYPE_NOT_ALLOWED")]
    [InlineData("a.png", "image/jpeg", "FILE_CONTENT_MISMATCH")]
    public async Task Upload_rejects_wrong_type_or_content_without_touching_storage(string fileName, string contentType, string code)
    {
        await using var harness = new Harness();
        using var host = StartHost(harness);
        var client = WithPermission(host, Permissions.Core.CompanyWrite);
        // Always JPEG bytes: a ".png" file with JPEG content must fail the magic-byte check.
        var response = await client.PostAsync(ImageRoute, ImageContent(JpegBytes, fileName, contentType));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(code, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(harness.Storage.Uploads);
        harness.Db.ChangeTracker.Clear();
        Assert.Null((await harness.Db.Tenants.SingleAsync(t => t.Id == harness.FirstTenant)).LandingImageKey);
    }

    [Fact]
    public async Task Upload_rejects_content_that_does_not_match_the_declared_webp_type()
    {
        await using var harness = new Harness();
        using var host = StartHost(harness);
        var client = WithPermission(host, Permissions.Core.CompanyWrite);

        var response = await client.PostAsync(ImageRoute, ImageContent(PngBytes, "a.webp", "image/webp"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("FILE_CONTENT_MISMATCH", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(harness.Storage.Uploads);
    }

    [Fact]
    public async Task Upload_rejects_files_over_three_megabytes_and_accepts_exactly_three()
    {
        await using var harness = new Harness();
        using var host = StartHost(harness);
        var client = WithPermission(host, Permissions.Core.CompanyWrite);

        var tooLarge = new byte[PortalLandingImageRules.MaxBytes + 1];
        PngBytes.CopyTo(tooLarge, 0);
        var rejected = await client.PostAsync(ImageRoute, ImageContent(tooLarge, "big.png", "image/png"));

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("FILE_TOO_LARGE", await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(harness.Storage.Uploads);

        var exact = new byte[PortalLandingImageRules.MaxBytes];
        PngBytes.CopyTo(exact, 0);
        var accepted = await client.PostAsync(ImageRoute, ImageContent(exact, "ok.png", "image/png"));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task Upload_without_a_file_is_a_bad_request()
    {
        await using var harness = new Harness();
        using var host = StartHost(harness);
        var client = WithPermission(host, Permissions.Core.CompanyWrite);

        var response = await client.PostAsync(ImageRoute, new MultipartFormDataContent());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(harness.Storage.Uploads);
    }

    [Fact]
    public async Task Replacing_the_image_deletes_the_previous_object_after_the_commit()
    {
        await using var harness = new Harness();
        using var host = StartHost(harness);
        var client = WithPermission(host, Permissions.Core.CompanyWrite);

        await client.PostAsync(ImageRoute, ImageContent(PngBytes, "a.png", "image/png"));
        var firstKey = Assert.Single(harness.Storage.Uploads).Key;
        Assert.Empty(harness.Storage.Deletes);

        var response = await client.PostAsync(ImageRoute, ImageContent(JpegBytes, "b.jpg", "image/jpeg"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var secondKey = harness.Storage.Uploads[1].Key;
        Assert.NotEqual(firstKey, secondKey);
        Assert.Equal((new StorageOptions().PublicBucket, firstKey), Assert.Single(harness.Storage.Deletes));
        Assert.DoesNotContain((new StorageOptions().PublicBucket, firstKey), harness.Storage.Objects);
        Assert.Contains((new StorageOptions().PublicBucket, secondKey), harness.Storage.Objects);
        harness.Db.ChangeTracker.Clear();
        Assert.Equal(secondKey, (await harness.Db.Tenants.SingleAsync(t => t.Id == harness.FirstTenant)).LandingImageKey);
    }

    [Fact]
    public async Task Delete_clears_the_key_and_removes_the_object_and_is_idempotent()
    {
        await using var harness = new Harness();
        using var host = StartHost(harness);
        var client = WithPermission(host, Permissions.Core.CompanyWrite);
        await client.PostAsync(ImageRoute, ImageContent(PngBytes, "a.png", "image/png"));
        var key = Assert.Single(harness.Storage.Uploads).Key;

        var response = await client.DeleteAsync(ImageRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<PortalAppearanceResponse>();
        Assert.NotNull(dto);
        Assert.Null(dto.LandingImageUrl);
        Assert.Equal((new StorageOptions().PublicBucket, key), Assert.Single(harness.Storage.Deletes));
        harness.Db.ChangeTracker.Clear();
        Assert.Null((await harness.Db.Tenants.SingleAsync(t => t.Id == harness.FirstTenant)).LandingImageKey);

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync(ImageRoute)).StatusCode);
        Assert.Single(harness.Storage.Deletes);
    }

    [Fact]
    public async Task Delete_requires_company_write_permission_and_keeps_the_image()
    {
        await using var harness = new Harness();
        var first = await harness.Db.Tenants.SingleAsync(t => t.Id == harness.FirstTenant);
        first.SetLandingImageKey("tenants/x/landing/keep.png");
        await harness.Db.SaveChangesAsync();
        using var host = StartHost(harness);
        var client = WithPermission(host, Permissions.Core.UnitsWrite);

        var response = await client.DeleteAsync(ImageRoute);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(harness.Storage.Deletes);
        harness.Db.ChangeTracker.Clear();
        Assert.Equal("tenants/x/landing/keep.png", (await harness.Db.Tenants.SingleAsync(t => t.Id == harness.FirstTenant)).LandingImageKey);
    }

    [Fact]
    public async Task Public_branding_returns_the_landing_fields_for_the_requested_tenant_only()
    {
        await using var harness = new Harness();
        var storage = new RecordingStorageProvider();
        var first = await harness.Db.Tenants.SingleAsync(t => t.Id == harness.FirstTenant);
        first.UpdateLandingTexts("Kicker", "Headline", "Subtitle");
        first.SetLandingImageKey("tenants/first/landing/hero.webp");
        var second = await harness.Db.Tenants.SingleAsync(t => t.Id == harness.SecondTenant);
        second.UpdateLandingTexts("Other kicker", "Other headline", "Other subtitle");
        await harness.Db.SaveChangesAsync();
        var auth = CustomerAuthHarness.CreateService(
            harness.Db,
            harness.Provider,
            new FakePhoneVerificationClient(),
            new FakeEmailProvider(),
            new AllowAllPhoneVerificationSendGate(),
            TimeProvider.System,
            storageProvider: storage,
            storageOptions: Options.Create(new StorageOptions { PublicBucket = "pub" }));

        var branding = await auth.GetBrandingAsync("first", default);
        var other = await auth.GetBrandingAsync("second", default);

        Assert.Equal("https://storage.test/pub/tenants/first/landing/hero.webp", branding.LandingImageUrl);
        Assert.Equal("Kicker", branding.LandingKicker);
        Assert.Equal("Headline", branding.LandingHeadline);
        Assert.Equal("Subtitle", branding.LandingSubtitle);
        Assert.Null(other.LandingImageUrl);
        Assert.Equal("Other kicker", other.LandingKicker);
    }

    [Fact]
    public async Task Public_branding_landing_fields_are_null_when_nothing_is_configured()
    {
        await using var harness = new Harness();
        var auth = CustomerAuthHarness.CreateService(
            harness.Db,
            harness.Provider,
            new FakePhoneVerificationClient(),
            new FakeEmailProvider(),
            new AllowAllPhoneVerificationSendGate(),
            TimeProvider.System,
            storageProvider: new RecordingStorageProvider(),
            storageOptions: Options.Create(new StorageOptions()));

        var branding = await auth.GetBrandingAsync("first", default);

        Assert.Null(branding.LandingImageUrl);
        Assert.Null(branding.LandingKicker);
        Assert.Null(branding.LandingHeadline);
        Assert.Null(branding.LandingSubtitle);
    }

    private static MultipartFormDataContent ImageContent(byte[] bytes, string fileName, string contentType)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { file, "file", fileName } };
    }

    private static HttpClient WithPermission(IHost host, string permission)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthentication.Header, permission);
        return client;
    }

    private static IHost StartHost(Harness harness, bool realPlanGuard = false) => new HostBuilder().ConfigureWebHost(web =>
    {
        web.UseTestServer();
        web.ConfigureServices(services =>
        {
            services.AddLogging();
            services.AddSingleton(harness.Db);
            services.AddSingleton<ITenantProvider>(harness.Provider);
            services.AddSingleton<IStorageProvider>(harness.Storage);
            services.AddSingleton(Options.Create(new StorageOptions()));
            if (realPlanGuard)
            {
                services.AddSingleton<IPlatformAdminChecker>(new FakePlatformAdminChecker());
                services.AddSingleton<IPlanGuard, PlanGuard>();
            }
            else
            {
                services.AddSingleton<IPlanGuard, FakePlanGuard>();
            }

            services.AddSingleton<IPortalAppearanceService, PortalAppearanceService>();
            services.AddSingleton<IAuthorizationHandler, HeaderPermissionHandler>();
            services.AddAuthentication(SupabaseJwtBearerDefaults.AuthenticationScheme)
                .AddScheme<AuthenticationSchemeOptions, TestAuthentication>(SupabaseJwtBearerDefaults.AuthenticationScheme, _ => { });
            services.AddAuthorization(options => options.AddRolvixPolicies());
            services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
            services.AddControllers().ConfigureApplicationPartManager(manager =>
            {
                manager.ApplicationParts.Clear();
                manager.ApplicationParts.Add(new AssemblyPart(typeof(PortalAppearanceController).Assembly));
                foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToArray()) manager.FeatureProviders.Remove(provider);
                manager.FeatureProviders.Add(new PortalControllerFeatureProvider());
            });
        });
        web.Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); });
    }).Start();

    private sealed class Harness : IAsyncDisposable
    {
        public Guid FirstTenant { get; }
        public Guid SecondTenant { get; }
        public FakeTenantProvider Provider { get; } = new();
        public RecordingStorageProvider Storage { get; } = new();
        public AppDbContext Db { get; }

        public Harness()
        {
            Db = InMemoryAppDb.Create(Provider);
            var first = new Tenant("First Co", FirstCnpj, "First", "first");
            var second = new Tenant("Second Co", SecondCnpj, "Second", "second");
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

    private sealed class PortalControllerFeatureProvider : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) => typeInfo.AsType() == typeof(PortalAppearanceController);
    }
}
