using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Modules.Users.Controllers;
using Platform.Api.Modules.Users.Services;
using Platform.Api.Notifications;
using Platform.Api.Services.Trial;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Users;

public sealed class CurrentUserPhoneTests
{
    private const string CallerEmail = "caller@studio.test";
    private const string ColleagueEmail = "colleague@studio.test";
    private const string PlatformEmail = "platform@studio.test";
    private const string TradeA = "Ateliê Norte";
    private const string LegalA = "Ateliê Norte Ltda";
    private const string ColleaguePhone = "+5511900001111";
    private const string OtherTenantPhone = "+5511900002222";
    private const string RawPhone = "(11) 98888-7777";
    private const string NormalizedPhone = "+5511988887777";

    [Fact]
    public void Put_me_is_a_staff_write_covered_by_the_session_guard()
    {
        var method = typeof(UsersController).GetMethod(nameof(UsersController.UpdateCurrentPhone));
        Assert.NotNull(method);
        Assert.NotNull(method!.GetCustomAttribute<HttpPutAttribute>());
        Assert.DoesNotContain(
            method.GetCustomAttributes(inherit: true),
            attribute => attribute is IAllowAnonymous);
        Assert.False(StaffWriteSessionGuard.IsSafeMethod(HttpMethods.Put));
        Assert.False(StaffWriteSessionGuard.IsSafeMethod(HttpMethods.Patch));
        Assert.True(StaffWriteSessionGuard.IsSafeMethod(HttpMethods.Get));
    }

    [Fact]
    public async Task Get_me_returns_null_phone_and_keeps_tenant_names()
    {
        var seed = await SeedAsync();
        var logs = new LogCapture();
        using var host = await StartHostAsync(seed.DatabaseName, logs);

        var response = await SendAsync(host, HttpMethod.Get, CallerEmail, seed.TenantA, body: null);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(seed.CallerId, doc.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("phone").ValueKind);
        Assert.Equal(TradeA, doc.RootElement.GetProperty("tenantTradeName").GetString());
        Assert.Equal(LegalA, doc.RootElement.GetProperty("tenantLegalName").GetString());
        Assert.DoesNotContain(ColleaguePhone, body, StringComparison.Ordinal);
        Assert.DoesNotContain(OtherTenantPhone, body, StringComparison.Ordinal);
        AssertNoPhoneInLogs(logs);
    }

    [Fact]
    public async Task Put_me_normalizes_phone_to_e164_and_returns_the_current_user()
    {
        var seed = await SeedAsync();
        var logs = new LogCapture();
        using var host = await StartHostAsync(seed.DatabaseName, logs);

        var response = await SendAsync(
            host,
            HttpMethod.Put,
            CallerEmail,
            seed.TenantA,
            $$"""{"phone":"{{RawPhone}}","userId":"{{seed.ColleagueId}}","id":"{{seed.OtherTenantUserId}}","tenantId":"{{seed.TenantB}}"}""");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(seed.CallerId, doc.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(NormalizedPhone, doc.RootElement.GetProperty("phone").GetString());
        Assert.Equal(seed.TenantA, doc.RootElement.GetProperty("tenantId").GetGuid());
        Assert.Equal(TradeA, doc.RootElement.GetProperty("tenantTradeName").GetString());
        Assert.Equal(LegalA, doc.RootElement.GetProperty("tenantLegalName").GetString());
        Assert.DoesNotContain(ColleaguePhone, body, StringComparison.Ordinal);
        Assert.DoesNotContain(OtherTenantPhone, body, StringComparison.Ordinal);

        var stored = await ReadPhonesAsync(seed.DatabaseName);
        Assert.Equal(NormalizedPhone, stored[seed.CallerId]);
        Assert.Equal(ColleaguePhone, stored[seed.ColleagueId]);
        Assert.Equal(OtherTenantPhone, stored[seed.OtherTenantUserId]);
        AssertNoPhoneInLogs(logs);
    }

    [Fact]
    public async Task Put_me_rejects_an_invalid_phone_without_changing_any_row()
    {
        var seed = await SeedAsync();
        var logs = new LogCapture();
        using var host = await StartHostAsync(seed.DatabaseName, logs);

        var response = await SendAsync(
            host,
            HttpMethod.Put,
            CallerEmail,
            seed.TenantA,
            """{"phone":"not-a-phone-ZZZ"}""");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(
            ["error"],
            doc.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("Phone must include DDD and number.", doc.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("not-a-phone-ZZZ", body, StringComparison.Ordinal);

        var stored = await ReadPhonesAsync(seed.DatabaseName);
        Assert.Null(stored[seed.CallerId]);
        Assert.Equal(ColleaguePhone, stored[seed.ColleagueId]);
        Assert.Equal(OtherTenantPhone, stored[seed.OtherTenantUserId]);
        AssertNoPhoneInLogs(logs);
    }

    [Fact]
    public async Task Put_me_clears_phone_when_null_and_leaves_other_rows()
    {
        var seed = await SeedAsync(callerPhone: NormalizedPhone);
        var logs = new LogCapture();
        using var host = await StartHostAsync(seed.DatabaseName, logs);

        var response = await SendAsync(
            host,
            HttpMethod.Put,
            CallerEmail,
            seed.TenantA,
            """{"phone":null}""");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(seed.CallerId, doc.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("phone").ValueKind);

        var stored = await ReadPhonesAsync(seed.DatabaseName);
        Assert.Null(stored[seed.CallerId]);
        Assert.Equal(ColleaguePhone, stored[seed.ColleagueId]);
        Assert.Equal(OtherTenantPhone, stored[seed.OtherTenantUserId]);
        AssertNoPhoneInLogs(logs);
    }

    [Fact]
    public async Task Put_me_without_a_phone_property_does_not_clear()
    {
        var seed = await SeedAsync(callerPhone: NormalizedPhone);
        using var host = await StartHostAsync(seed.DatabaseName, new LogCapture());

        var response = await SendAsync(
            host,
            HttpMethod.Put,
            CallerEmail,
            seed.TenantA,
            $$"""{"userId":"{{seed.ColleagueId}}"}""");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, body);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("error", out var error), body);
        Assert.Equal("Phone is required.", error.GetString());

        var stored = await ReadPhonesAsync(seed.DatabaseName);
        Assert.Equal(NormalizedPhone, stored[seed.CallerId]);
        Assert.Equal(ColleaguePhone, stored[seed.ColleagueId]);
        Assert.Equal(OtherTenantPhone, stored[seed.OtherTenantUserId]);
    }

    [Fact]
    public async Task Put_me_without_authentication_is_401_and_writes_nothing()
    {
        var seed = await SeedAsync(callerPhone: NormalizedPhone);
        using var host = await StartHostAsync(seed.DatabaseName, new LogCapture());

        var response = await SendAsync(
            host,
            HttpMethod.Put,
            email: null,
            tenantId: null,
            """{"phone":"(11) 90000-0000"}""");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var stored = await ReadPhonesAsync(seed.DatabaseName);
        Assert.Equal(NormalizedPhone, stored[seed.CallerId]);
        Assert.Equal(ColleaguePhone, stored[seed.ColleagueId]);
        Assert.Equal(OtherTenantPhone, stored[seed.OtherTenantUserId]);
    }

    [Fact]
    public async Task Put_me_without_a_tenant_is_401_and_writes_nothing()
    {
        var seed = await SeedAsync(callerPhone: NormalizedPhone);
        using var host = await StartHostAsync(seed.DatabaseName, new LogCapture());

        var response = await SendAsync(
            host,
            HttpMethod.Put,
            CallerEmail,
            tenantId: null,
            """{"phone":"(11) 90000-0000"}""");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(
            "The access token is missing a valid app_metadata.tenant_id.",
            doc.RootElement.GetProperty("error").GetString());
        var stored = await ReadPhonesAsync(seed.DatabaseName);
        Assert.Equal(NormalizedPhone, stored[seed.CallerId]);
        Assert.Equal(ColleaguePhone, stored[seed.ColleagueId]);
        Assert.Equal(OtherTenantPhone, stored[seed.OtherTenantUserId]);
    }

    private static void AssertNoPhoneInLogs(LogCapture logs)
    {
        var text = logs.Text;
        Assert.DoesNotContain(RawPhone, text, StringComparison.Ordinal);
        Assert.DoesNotContain(NormalizedPhone, text, StringComparison.Ordinal);
        Assert.DoesNotContain("11988887777", text, StringComparison.Ordinal);
        Assert.DoesNotContain(ColleaguePhone, text, StringComparison.Ordinal);
        Assert.DoesNotContain(OtherTenantPhone, text, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-phone-ZZZ", text, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        IHost host,
        HttpMethod method,
        string? email,
        Guid? tenantId,
        string? body)
    {
        var client = host.GetTestClient();
        if (email is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, email);
        }

        if (tenantId is Guid id)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, id.ToString());
        }

        var request = new HttpRequestMessage(method, "/api/users/me");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        return await client.SendAsync(request);
    }

    private static async Task<Dictionary<Guid, string?>> ReadPhonesAsync(string databaseName)
    {
        var provider = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(provider, databaseName);
        var rows = await db.Users.AsNoTracking().ToListAsync();
        return rows.ToDictionary(user => user.Id, user => user.Phone);
    }

    private static async Task<Seed> SeedAsync(string? callerPhone = null)
    {
        var databaseName = $"me-phone-{Guid.NewGuid():N}";
        var provider = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(provider, databaseName);
        var tenantA = new Tenant(LegalA, "11111111000191", TradeA, "atelie-norte");
        var tenantB = new Tenant("Galpão Sul Ltda", "22222222000191", "Galpão Sul", "galpao-sul");
        db.Tenants.AddRange(tenantA, tenantB);

        var otherTenantUser = new User(tenantB.Id, CallerEmail, "Caller Other", "caller.other@studio.test");
        otherTenantUser.SetPhone(OtherTenantPhone);
        var colleague = new User(tenantA.Id, ColleagueEmail, "Colleague", ColleagueEmail);
        colleague.SetPhone(ColleaguePhone);
        var caller = new User(tenantA.Id, CallerEmail, "Caller", CallerEmail);
        if (callerPhone is not null)
        {
            caller.SetPhone(callerPhone);
        }

        db.Users.Add(otherTenantUser);
        db.Users.Add(colleague);
        db.Users.Add(caller);
        await db.SaveChangesAsync();

        return new Seed(
            databaseName,
            tenantA.Id,
            tenantB.Id,
            caller.Id,
            colleague.Id,
            otherTenantUser.Id);
    }

    private static Task<IHost> StartHostAsync(string databaseName, LogCapture logs)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton(logs);
                    services.AddLogging(logging =>
                    {
                        logging.AddProvider(logs);
                        logging.SetMinimumLevel(LogLevel.Trace);
                    });
                    services.AddHttpContextAccessor();
                    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
                    services.AddSingleton<IHostEnvironment, FakeHostEnvironment>();
                    services.AddSingleton<NotificationQueue>();
                    services.AddSingleton<IPlatformAdminChecker>(_ => new FakePlatformAdminChecker(PlatformEmail));
                    services.AddScoped<AmbientTenantContext>();
                    services.AddScoped<PlatformAdminTenantSession>();
                    services.AddScoped<ITenantProvider, HttpContextTenantProvider>();
                    services.AddScoped<AppDbContext>(sp =>
                        InMemoryAppDb.Create(sp.GetRequiredService<ITenantProvider>(), databaseName));
                    services.AddScoped<ITenantModuleAccessor, TenantModuleAccessor>();
                    services.AddScoped<IPermissionResolver, PermissionResolver>();
                    services.AddScoped<IRbacGrantGuard, RbacGrantGuard>();
                    services.AddSingleton<ITrialGuard, FakeTrialGuard>();
                    services.AddScoped<IRbacActorAccessor, RbacActorAccessor>();
                    services.AddScoped<IUserDirectoryService, UserDirectoryService>();
                    services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
                    services.AddAuthorization(options => options.AddRolvixPolicies());
                    services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
                    services.AddSingleton<IAuthorizationMiddlewareResultHandler, JsonForbiddenAuthorizationResultHandler>();
                    services
                        .AddAuthentication(SupabaseJwtBearerDefaults.AuthenticationScheme)
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                            SupabaseJwtBearerDefaults.AuthenticationScheme,
                            _ => { });
                    services.AddControllers()
                        .ConfigureApplicationPartManager(manager =>
                        {
                            manager.ApplicationParts.Clear();
                            foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                            {
                                manager.FeatureProviders.Remove(provider);
                            }

                            manager.ApplicationParts.Add(new AssemblyPart(typeof(UsersController).Assembly));
                            manager.FeatureProviders.Add(new SelectedControllers(typeof(UsersController)));
                        });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .Start();
        return Task.FromResult(host);
    }

    private sealed record Seed(
        string DatabaseName,
        Guid TenantA,
        Guid TenantB,
        Guid CallerId,
        Guid ColleagueId,
        Guid OtherTenantUserId);

    private sealed class SelectedControllers(params Type[] controllerTypes) : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            controllerTypes.Contains(typeInfo.AsType());
    }

    private sealed class LogCapture : ILoggerProvider
    {
        private readonly List<string> _messages = [];

        public string Text
        {
            get
            {
                lock (_messages)
                {
                    return string.Join('\n', _messages);
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _messages);

        public void Dispose()
        {
        }

        private sealed class Logger(string categoryName, List<string> messages) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var rendered = formatter(state, exception);
                lock (messages)
                {
                    messages.Add($"{categoryName}: {rendered}");
                    if (exception is not null)
                    {
                        messages.Add(exception.ToString());
                    }
                }
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string UserHeader = "X-Test-User";
        public const string TenantHeader = "X-Test-Tenant";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var userValues)
                || string.IsNullOrWhiteSpace(userValues.ToString()))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var email = userValues.ToString();
            var claims = new List<Claim>
            {
                new("email", email),
                new(ClaimTypes.Email, email),
                new(ClaimTypes.Name, email),
                new("sub", email),
            };

            if (Request.Headers.TryGetValue(TenantHeader, out var tenantValues)
                && Guid.TryParse(tenantValues.ToString(), out var tenantId))
            {
                claims.Add(new Claim(
                    TenantClaimTypes.AppMetadata,
                    $"{{\"tenant_id\":\"{tenantId}\"}}"));
                Context.RequestServices.GetService<PlatformAdminTenantSession>()?.Resolve(tenantId);
            }

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
