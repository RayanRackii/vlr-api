using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
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
using Platform.Api.Modules.Rentals.Controllers;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Rentals;

public sealed class StaffRentalAssetQueueHttpTests
{
    private const string StaffEmail = "desk@club.test";
    private const string DeniedEmail = "limited@club.test";
    private const string SecretCustomer = "Foreign Only Person";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Staff_queue_declares_reservation_read()
    {
        var method = typeof(StaffRentalAssetQueueController).GetMethod(
            nameof(StaffRentalAssetQueueController.GetQueue));
        var permission = method!.GetCustomAttribute<RequirePermissionAttribute>();
        var module = typeof(StaffRentalAssetQueueController)
            .GetCustomAttribute<RequireActiveModuleAttribute>();

        Assert.Equal(
            PermissionPolicies.Name(Permissions.Rentals.ReservationsRead),
            permission!.Policy);
        Assert.Equal(PlatformModules.Rentals, module!.ModuleKey);
    }

    [Fact]
    public async Task Cross_tenant_resource_returns_404_without_leaking_the_roster()
    {
        var databaseName = NewDatabaseName();
        var clock = new TestTimeProvider(ReservationQueueHarness.Brazil(7, 31));
        await using var home = await ReservationQueueHarness.CreateAsync(databaseName: databaseName);
        home.Time.SetUtcNow(clock.GetUtcNow());
        await SeedStaffAsync(home);
        await home.Queue().JoinAsync(home.RentalAssetId, home.CustomerA, CancellationToken.None);

        var foreignProvider = new FakeTenantProvider();
        await using var foreign = await ReservationQueueHarness.CreateAsync(
            databaseName: databaseName,
            tenantProvider: foreignProvider);
        foreign.Time.SetUtcNow(clock.GetUtcNow());
        var customer = await foreign.Db.Customers.SingleAsync(item => item.Id == foreign.CustomerA);
        customer.Name = SecretCustomer;
        await foreign.Db.SaveChangesAsync();
        await foreign.Queue().JoinAsync(foreign.RentalAssetId, foreign.CustomerA, CancellationToken.None);

        using var host = StartHost(databaseName, clock);
        var client = Client(host, StaffEmail, home.TenantId);
        var response = await client.GetAsync(QueuePath(foreign.RentalAssetId));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Rentable was not found.", ReadError(body));
        Assert.DoesNotContain(SecretCustomer, body, StringComparison.Ordinal);
        Assert.DoesNotContain(foreign.TenantId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(foreign.CustomerA.ToString(), body, StringComparison.OrdinalIgnoreCase);

        var own = await client.GetFromJsonAsync<StaffReservationQueueDto>(
            QueuePath(home.RentalAssetId),
            JsonOptions);
        Assert.NotNull(own);
        Assert.Equal(home.RentalAssetId, own.RentalAssetId);
        Assert.DoesNotContain(own.Tickets, ticket => ticket.CustomerName == SecretCustomer);

        await using var foreignView = InMemoryAppDb.Create(
            new FakeTenantProvider { TenantId = foreign.TenantId },
            databaseName);
        var foreignTicket = await foreignView.ReservationQueueTickets.AsNoTracking().SingleAsync();
        Assert.Equal(QueueTicketStatus.Active, foreignTicket.Status);
        Assert.Equal(foreign.CustomerA, foreignTicket.CustomerId);
    }

    [Fact]
    public async Task Staff_without_reservation_read_is_forbidden()
    {
        var databaseName = NewDatabaseName();
        var clock = new TestTimeProvider(ReservationQueueHarness.Brazil(7, 31));
        await using var home = await ReservationQueueHarness.CreateAsync(databaseName: databaseName);
        home.Time.SetUtcNow(clock.GetUtcNow());
        await SeedStaffAsync(home);
        await home.Queue().JoinAsync(home.RentalAssetId, home.CustomerA, CancellationToken.None);

        using var host = StartHost(databaseName, clock);
        var client = Client(host, DeniedEmail, home.TenantId);
        var response = await client.GetAsync(QueuePath(home.RentalAssetId, ReservationQueueHarness.OpeningDate));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(RbacErrorCodes.Forbidden, ReadError(body));
        Assert.DoesNotContain("Cliente", body, StringComparison.Ordinal);
        Assert.DoesNotContain(home.RentalAssetId.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Empty_queue_returns_civil_today_and_does_not_create_a_session()
    {
        var databaseName = NewDatabaseName();
        var now = ReservationQueueHarness.Brazil(7, 10);
        var clock = new TestTimeProvider(now);
        await using var home = await ReservationQueueHarness.CreateAsync(databaseName: databaseName);
        await SeedStaffAsync(home);

        using var host = StartHost(databaseName, clock);
        var client = Client(host, StaffEmail, home.TenantId);

        var omitted = await GetQueueAsync(client, home.RentalAssetId);
        AssertEmptyWaitingRoom(omitted, home.RentalAssetId, now);

        var explicitToday = await GetQueueAsync(
            client,
            home.RentalAssetId,
            ReservationQueueHarness.OpeningDate);
        AssertEmptyWaitingRoom(explicitToday, home.RentalAssetId, now);

        var future = await GetQueueAsync(
            client,
            home.RentalAssetId,
            ReservationQueueHarness.OpeningDate.AddDays(1));
        Assert.Empty(future.Tickets);
        Assert.Equal(0, future.WaitingCount);
        Assert.Equal(0, future.ActiveCount);
        Assert.Equal(QueuePhase.Closed, future.Phase);
        Assert.Equal(ReservationQueueHarness.OpeningDate.AddDays(1), future.OpeningDate);

        var missing = await client.GetAsync(QueuePath(home.AssetId));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        await using var verify = InMemoryAppDb.Create(
            new FakeTenantProvider { TenantId = home.TenantId },
            databaseName);
        Assert.Equal(0, await verify.ReservationQueueSessions.CountAsync());
        Assert.Equal(0, await verify.ReservationQueueTickets.CountAsync());
    }

    [Fact]
    public async Task Open_queue_lists_waiting_and_active_tickets_without_writing()
    {
        var databaseName = NewDatabaseName();
        var now = ReservationQueueHarness.Brazil(7, 31);
        var clock = new TestTimeProvider(now);
        await using var home = await ReservationQueueHarness.CreateAsync(databaseName: databaseName);
        home.Time.SetUtcNow(now);
        await SeedStaffAsync(home);
        await home.Queue().JoinAsync(home.RentalAssetId, home.CustomerA, CancellationToken.None);
        await home.Queue().JoinAsync(home.RentalAssetId, home.CustomerB, CancellationToken.None);
        await home.Queue().JoinAsync(home.RentalAssetId, home.CustomerC, CancellationToken.None);

        using var host = StartHost(databaseName, clock);
        var client = Client(host, StaffEmail, home.TenantId);
        var raw = await client.GetStringAsync(
            QueuePath(home.RentalAssetId, ReservationQueueHarness.OpeningDate));
        var roster = JsonSerializer.Deserialize<StaffReservationQueueDto>(raw, JsonOptions);

        Assert.NotNull(roster);
        Assert.Contains("\"phase\":\"Open\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"Active\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"Waiting\"", raw, StringComparison.Ordinal);
        Assert.Equal(home.RentalAssetId, roster.RentalAssetId);
        Assert.True(roster.QueueEnabled);
        Assert.Equal(ReservationQueueHarness.OpeningDate, roster.OpeningDate);
        Assert.Equal(ReservationQueueHarness.Brazil(7, 30), roster.OpensAt);
        Assert.Equal(ReservationQueueHarness.Brazil(7, 0), roster.WaitingRoomOpensAt);
        Assert.Equal(now, roster.ServerNow);
        Assert.Equal(QueuePhase.Open, roster.Phase);
        Assert.Equal(2, roster.WaitingCount);
        Assert.Equal(1, roster.ActiveCount);
        Assert.Equal(3, roster.Tickets.Count);

        var active = Assert.Single(roster.Tickets, ticket => ticket.Status == QueueTicketStatus.Active);
        Assert.Equal(home.CustomerA, active.CustomerId);
        Assert.Equal("Cliente 1", active.CustomerName);
        Assert.Equal(1, active.Sequence);
        Assert.Equal(1, active.Position);
        Assert.Equal(now, active.TurnStartedAt);
        Assert.Equal(now + ReservationQueueClock.TurnDuration, active.TurnExpiresAt);
        Assert.Null(active.CompletedReservationId);

        var waiting = roster.Tickets.Where(ticket => ticket.Status == QueueTicketStatus.Waiting).ToList();
        Assert.Equal(2, waiting.Count);
        Assert.Equal([home.CustomerB, home.CustomerC], waiting.Select(ticket => ticket.CustomerId));
        Assert.Equal(["Cliente 2", "Cliente 3"], waiting.Select(ticket => ticket.CustomerName));
        Assert.Equal([2L, 3L], waiting.Select(ticket => ticket.Sequence));
        Assert.Equal([1, 2], waiting.Select(ticket => ticket.Position));
        Assert.All(waiting, ticket => Assert.Null(ticket.TurnStartedAt));
        Assert.All(waiting, ticket => Assert.Null(ticket.CompletedReservationId));

        var stored = await ReadStoredStatusesAsync(databaseName, home.TenantId);
        Assert.Equal(
            [QueueTicketStatus.Active, QueueTicketStatus.Waiting, QueueTicketStatus.Waiting],
            stored);

        var later = now.AddSeconds(91);
        clock.SetUtcNow(later);
        var projected = await GetQueueAsync(
            client,
            home.RentalAssetId,
            ReservationQueueHarness.OpeningDate);

        Assert.Equal(later, projected.ServerNow);
        Assert.Equal(1, projected.WaitingCount);
        Assert.Equal(1, projected.ActiveCount);
        Assert.Equal(QueueTicketStatus.Expired, projected.Tickets[0].Status);
        Assert.Equal(0, projected.Tickets[0].Position);
        Assert.Equal(home.CustomerB, projected.Tickets[1].CustomerId);
        Assert.Equal(QueueTicketStatus.Active, projected.Tickets[1].Status);
        Assert.Equal(1, projected.Tickets[1].Position);
        Assert.Equal(later, projected.Tickets[1].TurnStartedAt);
        Assert.Equal(later + ReservationQueueClock.TurnDuration, projected.Tickets[1].TurnExpiresAt);
        Assert.Equal(QueueTicketStatus.Waiting, projected.Tickets[2].Status);
        Assert.Equal(1, projected.Tickets[2].Position);
        Assert.Equal(stored, await ReadStoredStatusesAsync(databaseName, home.TenantId));
    }

    private static void AssertEmptyWaitingRoom(
        StaffReservationQueueDto roster,
        Guid rentalAssetId,
        DateTimeOffset now)
    {
        Assert.Equal(rentalAssetId, roster.RentalAssetId);
        Assert.True(roster.QueueEnabled);
        Assert.Equal(ReservationQueueHarness.OpeningDate, roster.OpeningDate);
        Assert.Equal(ReservationQueueHarness.Brazil(7, 30), roster.OpensAt);
        Assert.Equal(ReservationQueueHarness.Brazil(7, 0), roster.WaitingRoomOpensAt);
        Assert.Equal(now, roster.ServerNow);
        Assert.Equal(QueuePhase.WaitingRoom, roster.Phase);
        Assert.Equal(0, roster.WaitingCount);
        Assert.Equal(0, roster.ActiveCount);
        Assert.Empty(roster.Tickets);
    }

    private static async Task<StaffReservationQueueDto> GetQueueAsync(
        HttpClient client,
        Guid rentalAssetId,
        DateOnly? openingDate = null)
    {
        var roster = await client.GetFromJsonAsync<StaffReservationQueueDto>(
            QueuePath(rentalAssetId, openingDate),
            JsonOptions);
        return roster ?? throw new InvalidOperationException("Queue response was empty.");
    }

    private static async Task<List<QueueTicketStatus>> ReadStoredStatusesAsync(
        string databaseName,
        Guid tenantId)
    {
        await using var verify = InMemoryAppDb.Create(
            new FakeTenantProvider { TenantId = tenantId },
            databaseName);
        return await verify.ReservationQueueTickets
            .AsNoTracking()
            .OrderBy(ticket => ticket.Sequence)
            .Select(ticket => ticket.Status)
            .ToListAsync();
    }

    private static async Task SeedStaffAsync(ReservationQueueHarness home)
    {
        home.Db.TenantModules.Add(new TenantModule(home.TenantId, PlatformModules.Rentals, isActive: true));
        var read = new Permission(
            Permissions.Rentals.ReservationsRead,
            "Read reservations",
            moduleKey: PlatformModules.Rentals);
        var other = new Permission(
            Permissions.Rentals.AssetsRead,
            "Read rental assets",
            moduleKey: PlatformModules.Rentals);
        var desk = new Role(home.TenantId, "Desk");
        var limited = new Role(home.TenantId, "Limited");
        var staff = new User(home.TenantId, StaffEmail, "Desk", StaffEmail);
        var denied = new User(home.TenantId, DeniedEmail, "Limited", DeniedEmail);
        home.Db.Permissions.AddRange(read, other);
        home.Db.Roles.AddRange(desk, limited);
        home.Db.Users.AddRange(staff, denied);
        home.Db.RolePermissions.Add(new RolePermission(desk.Id, read.Id));
        home.Db.RolePermissions.Add(new RolePermission(limited.Id, other.Id));
        home.Db.UserRoles.Add(new UserRole(staff.Id, desk.Id));
        home.Db.UserRoles.Add(new UserRole(denied.Id, limited.Id));
        await home.Db.SaveChangesAsync();
    }

    private static string NewDatabaseName() => $"staff-queue-{Guid.NewGuid():N}";

    private static string QueuePath(Guid rentalAssetId, DateOnly? openingDate = null)
    {
        var path = $"/api/rentals/rental-assets/{rentalAssetId}/queue";
        return openingDate is DateOnly date
            ? $"{path}?openingDate={date:yyyy-MM-dd}"
            : path;
    }

    private static string? ReadError(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("error").GetString();
    }

    private static HttpClient Client(IHost host, string email, Guid tenantId)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, email);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, tenantId.ToString());
        return client;
    }

    private static IHost StartHost(string databaseName, TimeProvider clock) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddHttpContextAccessor();
                    services.AddSingleton(clock);
                    services.AddSingleton<IPlatformAdminChecker>(_ => new FakePlatformAdminChecker());
                    services.AddScoped<AmbientTenantContext>();
                    services.AddScoped<PlatformAdminTenantSession>();
                    services.AddScoped<ITenantProvider, HttpContextTenantProvider>();
                    services.AddScoped<AppDbContext>(sp =>
                        InMemoryAppDb.Create(sp.GetRequiredService<ITenantProvider>(), databaseName));
                    services.AddScoped<ITenantModuleAccessor, TenantModuleAccessor>();
                    services.AddScoped<IPermissionResolver, PermissionResolver>();
                    services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
                    services.AddScoped<IReservationQueueService, ReservationQueueService>();
                    services.AddAuthorization(options => options.AddRolvixPolicies());
                    services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
                    services.AddSingleton<IAuthorizationMiddlewareResultHandler, JsonForbiddenAuthorizationResultHandler>();
                    services
                        .AddAuthentication(SupabaseJwtBearerDefaults.AuthenticationScheme)
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                            SupabaseJwtBearerDefaults.AuthenticationScheme,
                            _ => { });
                    services.AddControllers()
                        .AddJsonOptions(options =>
                        {
                            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                        })
                        .ConfigureApplicationPartManager(manager =>
                        {
                            manager.ApplicationParts.Clear();
                            foreach (var provider in manager.FeatureProviders
                                         .OfType<ControllerFeatureProvider>()
                                         .ToList())
                            {
                                manager.FeatureProviders.Remove(provider);
                            }

                            manager.ApplicationParts.Add(
                                new AssemblyPart(typeof(StaffRentalAssetQueueController).Assembly));
                            manager.FeatureProviders.Add(
                                new SingleController(typeof(StaffRentalAssetQueueController)));
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

    private sealed class SingleController(Type controllerType) : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            typeInfo.AsType() == controllerType;
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
            }

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
