using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Modules.Rentals;
using Platform.Api.Modules.Rentals.Controllers;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Notifications;
using Platform.Api.Services.Trial;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Rentals;

public sealed class ExpectedPriceHttpTests
{
    private const string Secret = "expected-price-test-secret-longer-than-32-bytes";

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Real_post_returns_409_current_total_then_accepts_explicit_corrected_retry(bool persisted)
    {
        var database = $"expected-price-{Guid.NewGuid():N}";
        await using var h = await ReservationQueueHarness.CreateAsync(
            queueEnabled: false, includeSlot: true, databaseName: database);
        using var host = StartHost(h, database);
        using var client = await CustomerClientAsync(host, h);
        // The quote came from the daily API before staff changed the price.
        var quote = await new DailyAvailabilityService(h.Db, h.TenantProvider, h.Time).GetAsync(
            ReservationQueueHarness.BookDate, [h.RentalAssetId], h.UnitId, 1, 60, CancellationToken.None);
        var expected = quote.Resources.Single().Windows.Single(w => w.SlotId == h.SlotId).TotalAmount;
        (await h.Db.RentalPricings.SingleAsync()).PricePerHour = 125m;
        await h.Db.SaveChangesAsync();

        var response = await client.PostAsJsonAsync(Route(persisted), Payload(h, persisted, expected));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("PRICE_CHANGED", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(125m, body.RootElement.GetProperty("currentTotalAmount").GetDecimal());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("error").GetString()));
        Assert.Empty(await h.Db.Reservations.ToListAsync());
        var retry = await client.PostAsJsonAsync(Route(persisted), Payload(h, persisted, 125m));
        Assert.Equal(persisted ? HttpStatusCode.OK : HttpStatusCode.Created, retry.StatusCode);
        using var created = JsonDocument.Parse(await retry.Content.ReadAsStringAsync());
        Assert.Equal(125m, created.RootElement.GetProperty("totalAmount").GetDecimal());
        Assert.Single(await h.Db.Reservations.ToListAsync());
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Omitted_or_explicit_null_preserves_legacy_post(bool persisted, bool explicitNull)
    {
        var database = $"expected-price-{Guid.NewGuid():N}";
        await using var h = await ReservationQueueHarness.CreateAsync(
            queueEnabled: false, includeSlot: true, databaseName: database);
        using var host = StartHost(h, database);
        using var client = await CustomerClientAsync(host, h);
        var payload = Payload(h, persisted, null);
        if (!explicitNull) payload.Remove("expectedTotalAmount");
        var response = await client.PostAsJsonAsync(Route(persisted), payload);
        Assert.Equal(persisted ? HttpStatusCode.OK : HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(100m, (await h.Db.Reservations.SingleAsync()).TotalAmount);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Foreign_resource_ids_and_hostile_subdomain_header_cannot_reveal_a_price(bool persisted)
    {
        var database = $"expected-price-{Guid.NewGuid():N}";
        await using var owner = await ReservationQueueHarness.CreateAsync(
            queueEnabled: false, includeSlot: true, databaseName: database);
        await using var other = await ReservationQueueHarness.CreateAsync(
            queueEnabled: false, includeSlot: true, databaseName: database);
        using var host = StartHost(owner, database);
        using var client = await CustomerClientAsync(host, other);
        client.DefaultRequestHeaders.Add(TenantHeaders.Subdomain,
            (await owner.Db.Tenants.SingleAsync(t => t.Id == owner.TenantId)).Subdomain);
        var response = await client.PostAsJsonAsync(Route(persisted), Payload(owner, persisted, 1));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("currentTotalAmount", await response.Content.ReadAsStringAsync());
        Assert.Empty(await owner.Db.Reservations.ToListAsync());
        Assert.Empty(await other.Db.Reservations.ToListAsync());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Wrong_unit_and_missing_auth_do_not_return_current_price(bool persisted)
    {
        var database = $"expected-price-{Guid.NewGuid():N}";
        await using var h = await ReservationQueueHarness.CreateAsync(
            queueEnabled: false, includeSlot: true, databaseName: database);
        using var host = StartHost(h, database);
        using var client = await CustomerClientAsync(host, h);
        var payload = Payload(h, persisted, 1);
        payload["unitId"] = Guid.NewGuid();
        var wrongUnit = await client.PostAsJsonAsync(Route(persisted), payload);
        Assert.Equal(persisted ? HttpStatusCode.Conflict : HttpStatusCode.NotFound, wrongUnit.StatusCode);
        Assert.DoesNotContain("currentTotalAmount", await wrongUnit.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync(Route(persisted), Payload(h, persisted, 1))).StatusCode);
        Assert.Empty(await h.Db.Reservations.ToListAsync());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Malformed_expected_amount_is_400_without_a_write(bool persisted)
    {
        var database = $"expected-price-{Guid.NewGuid():N}";
        await using var h = await ReservationQueueHarness.CreateAsync(
            queueEnabled: false, includeSlot: true, databaseName: database);
        using var host = StartHost(h, database);
        using var client = await CustomerClientAsync(host, h);
        var payload = Payload(h, persisted, 1);
        payload["expectedTotalAmount"] = "invalid-price";
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync(Route(persisted), payload)).StatusCode);
        Assert.Empty(await h.Db.Reservations.ToListAsync());
    }

    private static string Route(bool persisted) => persisted ? "/api/schedule/slots/book" : "/api/reservations";

    private static Dictionary<string, object?> Payload(ReservationQueueHarness h, bool persisted, decimal? expected)
    {
        var payload = new Dictionary<string, object?> { ["unitId"] = h.UnitId, ["expectedTotalAmount"] = expected };
        if (persisted) payload["slotId"] = h.SlotId;
        else
        {
            payload["date"] = ReservationQueueHarness.BookDate;
            payload["startTime"] = ReservationQueueHarness.BookStart;
            payload["endTime"] = ReservationQueueHarness.BookEnd;
            payload["items"] = new[] { new { assetId = h.AssetId, quantity = 1 } };
        }
        return payload;
    }

    private static async Task<HttpClient> CustomerClientAsync(IHost host, ReservationQueueHarness h)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            new CustomerJwtIssuer(Configuration()).IssueToken(await h.Db.Customers.SingleAsync(c => c.Id == h.CustomerA)));
        return client;
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["Supabase:JwtSecret"] = Secret }).Build();

    private static IHost StartHost(ReservationQueueHarness h, string database) => new HostBuilder().ConfigureWebHost(web =>
    {
        web.UseTestServer();
        web.ConfigureServices(services =>
        {
            services.AddLogging();
            services.AddSingleton(Configuration());
            services.AddHttpContextAccessor();
            services.AddScoped<AmbientTenantContext>();
            services.AddScoped<PlatformAdminTenantSession>();
            services.AddSingleton<IPlatformAdminChecker>(new FakePlatformAdminChecker());
            services.AddScoped<ITenantProvider, HttpContextTenantProvider>();
            services.AddScoped(sp => InMemoryAppDb.Create(sp.GetRequiredService<ITenantProvider>(), database));
            services.AddScoped<IPublicTenantBinder, PublicTenantBinder>();
            services.AddSingleton<ITenantModuleAccessor>(new ActiveRentalsModule());
            services.AddSingleton<TimeProvider>(h.Time);
            services.AddRentalsModule();
            services.AddSingleton<ITrialGuard, FakeTrialGuard>();
            services.AddSingleton<IRentalsNotificationPublisher>(SilentRentalsNotifications.Publisher);
            services.AddSingleton<INotificationOutboxScheduler>(SilentRentalsNotifications.Scheduler);
            services.AddAuthentication(CustomerJwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(CustomerJwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.MapInboundClaims = false;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true, ValidIssuer = CustomerJwtIssuer.Issuer,
                        ValidateAudience = true, ValidAudience = CustomerJwtIssuer.Audience,
                        ValidateLifetime = true, ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)),
                    };
                });
            services.AddAuthorization(options => options.AddRolvixPolicies());
            services.AddControllers().ConfigureApplicationPartManager(manager =>
            {
                manager.ApplicationParts.Clear();
                foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToArray())
                    manager.FeatureProviders.Remove(provider);
                manager.ApplicationParts.Add(new AssemblyPart(typeof(ReservationsController).Assembly));
                manager.FeatureProviders.Add(new BookingControllers());
            });
        });
        web.Configure(app =>
        {
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        });
    }).Start();

    private sealed class BookingControllers : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo type) =>
            type.AsType() == typeof(ReservationsController) || type.AsType() == typeof(ScheduleController);
    }

    private sealed class ActiveRentalsModule : ITenantModuleAccessor
    {
        public Task<IReadOnlySet<string>> GetActiveModuleKeysAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string> { "rentals" });
    }
}
