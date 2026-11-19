using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
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
using Platform.Api.Modules.ResourcePlanning;
using Platform.Api.Modules.ResourcePlanning.Contracts;
using Platform.Api.Modules.ResourcePlanning.Controllers;
using Platform.Api.Modules.ResourcePlanning.Dtos;
using Platform.Api.Modules.ResourcePlanning.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.ResourcePlanning;

/// <summary>
/// HTTP tests of the allocation endpoints and the agenda with the real permission pipeline: the
/// attribute gates, the module gate, the technician flow (create, start, release; cannot cancel
/// someone else's allocation), the error bodies, and tenant isolation on every endpoint with a
/// second tenant that has data of its own.
/// </summary>
public sealed class PlanningAllocationsHttpTests
{
    private const string TechEmail = "tech-one@shop.test";
    private const string OtherTechEmail = "tech-two@shop.test";
    private const string ManagerEmail = "manager@shop.test";
    private const string ReaderEmail = "reader@shop.test";
    private const string NoRoleEmail = "norole@shop.test";
    private const string ForeignTitle = "Foreign Secret Job";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // A Monday at 12:00Z (09:00 in Sao Paulo) well in the future, so no row is ever expired.
    private static readonly DateTimeOffset Monday = NextMonday(DateTimeOffset.UtcNow.AddDays(14));

    [Fact]
    public void Controller_is_gated_by_resource_planning_and_each_action_by_its_permission()
    {
        Assert.Equal(
            PlatformModules.ResourcePlanning,
            typeof(PlanningAllocationsController).GetCustomAttribute<RequireActiveModuleAttribute>()!.ModuleKey);

        var read = PermissionPolicies.Name(Permissions.ResourcePlanning.AgendaRead);
        Assert.Equal(read, Policy(nameof(PlanningAllocationsController.Agenda)));
        Assert.Equal(read, Policy(nameof(PlanningAllocationsController.Preview)));
        Assert.Equal(read, Policy(nameof(PlanningAllocationsController.List)));
        Assert.Equal(read, Policy(nameof(PlanningAllocationsController.Get)));
        Assert.Equal(PermissionPolicies.Name(Permissions.ResourcePlanning.AllocationsManage), Policy(nameof(PlanningAllocationsController.Update)));

        // Create: any-of manage or execute. Usage: any-of execute or manage.
        Assert.Equal(
            PermissionPolicies.AnyName(Permissions.ResourcePlanning.AllocationsManage, Permissions.ResourcePlanning.AllocationsExecute),
            Policy(nameof(PlanningAllocationsController.Create)));
        Assert.Equal(
            PermissionPolicies.AnyName(Permissions.ResourcePlanning.AllocationsExecute, Permissions.ResourcePlanning.AllocationsManage),
            Policy(nameof(PlanningAllocationsController.Usage)));

        static string Policy(string action) =>
            typeof(PlanningAllocationsController).GetMethod(action)!
                .GetCustomAttributes().OfType<IAuthorizeData>().Single().Policy!;
    }

    [Fact]
    public async Task Technician_creates_starts_finishes_and_releases_an_allocation()
    {
        var world = await SeedAsync();
        using var host = StartHost(world.DatabaseName);
        var tech = Client(host, TechEmail, world.TenantId);

        var created = await CreateAsync(tech, world.AssetId, Monday, Monday.AddHours(2), "Print TPU case");
        Assert.Equal("Scheduled", created.Status);
        Assert.Equal(world.TechUserId, created.CreatedByUserId);

        var started = await UsageAsync(tech, created.Id, "start", created.Version);
        Assert.Equal("Running", started.Status);
        var finished = await UsageAsync(tech, created.Id, "work-finished", started.Version);
        Assert.Equal("Cleaning", finished.Status);
        var released = await UsageAsync(tech, created.Id, "release", finished.Version);
        Assert.Equal("Released", released.Status);

        await using var verify = Verify(world.DatabaseName, world.TenantId);
        var row = await verify.AssetAllocations.SingleAsync();
        Assert.Equal(AllocationStatus.Released, row.Status);
        Assert.Equal(world.TenantId, row.TenantId);
        Assert.Equal(world.TechUserId, row.CreatedByUserId);
    }

    [Fact]
    public async Task Technician_cannot_cancel_someone_elses_allocation_but_can_cancel_their_own_and_a_manager_any()
    {
        var world = await SeedAsync();
        using var host = StartHost(world.DatabaseName);
        var tech = Client(host, TechEmail, world.TenantId);
        var otherTech = Client(host, OtherTechEmail, world.TenantId);
        var manager = Client(host, ManagerEmail, world.TenantId);

        var mine = await CreateAsync(tech, world.AssetId, Monday, Monday.AddHours(1), "Mine");

        var denied = await PostUsageAsync(otherTech, mine.Id, "cancel", mine.Version);
        var body = await denied.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(PlanningException.CancelForbiddenCode, ReadCode(body));
        Assert.Equal("Scheduled", (await GetAsync(tech, mine.Id)).Status);

        // The same technician can still start the colleague's allocation (execute).
        var started = await UsageAsync(otherTech, mine.Id, "start", mine.Version);
        Assert.Equal("Running", started.Status);

        var own = await CreateAsync(tech, world.AssetId, Monday.AddHours(3), Monday.AddHours(4), "Own", assetOverride: world.SecondAssetId);
        Assert.Equal("Cancelled", (await UsageAsync(tech, own.Id, "cancel", own.Version, "changed my mind")).Status);

        var theirs = await CreateAsync(otherTech, world.AssetId, Monday.AddHours(3), Monday.AddHours(4), "Theirs", assetOverride: world.SecondAssetId);
        Assert.Equal("Cancelled", (await UsageAsync(manager, theirs.Id, "cancel", theirs.Version)).Status);
    }

    [Fact]
    public async Task Technician_cannot_edit_an_allocation_because_edit_needs_manage()
    {
        var world = await SeedAsync();
        using var host = StartHost(world.DatabaseName);
        var tech = Client(host, TechEmail, world.TenantId);
        var manager = Client(host, ManagerEmail, world.TenantId);
        var created = await CreateAsync(tech, world.AssetId, Monday, Monday.AddHours(1), "Original");

        var denied = await PatchAsync(
            tech,
            $"/api/planning/allocations/{created.Id}",
            new { expectedVersion = created.Version, title = "Hacked" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(RbacErrorCodes.Forbidden, ReadError(await denied.Content.ReadAsStringAsync()));

        var allowed = await PatchAsync(
            manager,
            $"/api/planning/allocations/{created.Id}",
            new { expectedVersion = created.Version, title = "Renamed by manager" });
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal("Renamed by manager", (await allowed.Content.ReadFromJsonAsync<AssetAllocationResponse>(JsonOptions))!.Title);
    }

    [Fact]
    public async Task Reader_can_read_and_preview_but_cannot_create_edit_or_drive_allocations()
    {
        var world = await SeedAsync();
        using var host = StartHost(world.DatabaseName);
        var tech = Client(host, TechEmail, world.TenantId);
        var reader = Client(host, ReaderEmail, world.TenantId);
        var created = await CreateAsync(tech, world.AssetId, Monday, Monday.AddHours(1), "Visible");

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/planning/allocations/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/planning/allocations")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(AgendaPath(Monday))).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await reader.PostAsJsonAsync("/api/planning/allocations/preview", CreateBody(world.AssetId, Monday.AddHours(5), Monday.AddHours(6), "x"), JsonOptions)).StatusCode);

        foreach (var response in new[]
        {
            await reader.PostAsJsonAsync("/api/planning/allocations", CreateBody(world.AssetId, Monday.AddHours(5), Monday.AddHours(6), "x"), JsonOptions),
            await reader.PostAsJsonAsync($"/api/planning/allocations/{created.Id}/usage", new { action = "start", expectedVersion = created.Version }, JsonOptions),
            await PatchAsync(reader, $"/api/planning/allocations/{created.Id}", new { expectedVersion = created.Version, title = "x" }),
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(RbacErrorCodes.Forbidden, ReadError(await response.Content.ReadAsStringAsync()));
        }

        await using var verify = Verify(world.DatabaseName, world.TenantId);
        var row = await verify.AssetAllocations.SingleAsync();
        Assert.Equal(AllocationStatus.Scheduled, row.Status);
        Assert.Equal("Visible", row.Title);
    }

    [Fact]
    public async Task User_without_planning_roles_is_forbidden_on_every_endpoint_and_nothing_leaks()
    {
        var world = await SeedAsync();
        using var host = StartHost(world.DatabaseName);
        var tech = Client(host, TechEmail, world.TenantId);
        var noRole = Client(host, NoRoleEmail, world.TenantId);
        var created = await CreateAsync(tech, world.AssetId, Monday, Monday.AddHours(1), "Visible to staff only");

        foreach (var response in new[]
        {
            await noRole.GetAsync(AgendaPath(Monday)),
            await noRole.GetAsync("/api/planning/allocations"),
            await noRole.GetAsync($"/api/planning/allocations/{created.Id}"),
            await noRole.PostAsJsonAsync("/api/planning/allocations/preview", CreateBody(world.AssetId, Monday, Monday.AddHours(1), "x"), JsonOptions),
            await noRole.PostAsJsonAsync("/api/planning/allocations", CreateBody(world.AssetId, Monday.AddHours(5), Monday.AddHours(6), "x"), JsonOptions),
            await PatchAsync(noRole, $"/api/planning/allocations/{created.Id}", new { expectedVersion = created.Version, title = "x" }),
            await noRole.PostAsJsonAsync($"/api/planning/allocations/{created.Id}/usage", new { action = "cancel", expectedVersion = created.Version }, JsonOptions),
        })
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(RbacErrorCodes.Forbidden, ReadError(body));
            Assert.DoesNotContain("Visible to staff only", body, StringComparison.Ordinal);
        }

        await using var verify = Verify(world.DatabaseName, world.TenantId);
        Assert.Equal(AllocationStatus.Scheduled, (await verify.AssetAllocations.SingleAsync()).Status);
    }

    [Fact]
    public async Task Module_off_is_forbidden_before_the_action_on_every_endpoint()
    {
        var world = await SeedAsync(planningActive: false);
        using var host = StartHost(world.DatabaseName);
        var tech = Client(host, TechEmail, world.TenantId);
        var id = Guid.NewGuid();

        foreach (var response in new[]
        {
            await tech.GetAsync(AgendaPath(Monday)),
            await tech.GetAsync("/api/planning/allocations"),
            await tech.GetAsync($"/api/planning/allocations/{id}"),
            await tech.PostAsJsonAsync("/api/planning/allocations/preview", CreateBody(world.AssetId, Monday, Monday.AddHours(1), "x"), JsonOptions),
            await tech.PostAsJsonAsync("/api/planning/allocations", CreateBody(world.AssetId, Monday, Monday.AddHours(1), "x"), JsonOptions),
            await tech.PostAsJsonAsync($"/api/planning/allocations/{id}/usage", new { action = "start", expectedVersion = "x" }, JsonOptions),
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        await using var verify = Verify(world.DatabaseName, world.TenantId);
        Assert.Empty(await verify.AssetAllocations.ToListAsync());
    }

    [Fact]
    public async Task Overlap_returns_409_with_the_conflicts_and_hours_violation_returns_422_with_the_reason()
    {
        var world = await SeedAsync(policy: AssetHoursPolicy.StartWithinHours, weekdayHours: true);
        using var host = StartHost(world.DatabaseName);
        var tech = Client(host, TechEmail, world.TenantId);
        await CreateAsync(tech, world.AssetId, Monday, Monday.AddHours(2), "Held job");

        var conflict = await tech.PostAsJsonAsync(
            "/api/planning/allocations",
            CreateBody(world.AssetId, Monday.AddHours(1), Monday.AddHours(3), "Too late"),
            JsonOptions);
        var conflictBody = await conflict.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(PlanningException.ConflictCode, ReadCode(conflictBody));
        using (var doc = JsonDocument.Parse(conflictBody))
        {
            var item = doc.RootElement.GetProperty("conflicts").EnumerateArray().Single();
            Assert.Equal("allocation", item.GetProperty("source").GetString());
            Assert.Equal("Held job", item.GetProperty("label").GetString());
        }

        var saturday = Monday.AddDays(5);
        var hours = await tech.PostAsJsonAsync(
            "/api/planning/allocations",
            CreateBody(world.AssetId, saturday, saturday.AddHours(1), "Weekend"),
            JsonOptions);
        var hoursBody = await hours.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, hours.StatusCode);
        Assert.Equal(PlanningException.OutsideWorkingHoursCode, ReadCode(hoursBody));
        using (var doc = JsonDocument.Parse(hoursBody))
        {
            Assert.Equal(AssetWorkingHoursRules.StartOutsideHours, doc.RootElement.GetProperty("hoursViolation").GetString());
        }

        await using var verify = Verify(world.DatabaseName, world.TenantId);
        Assert.Equal(1, await verify.AssetAllocations.CountAsync());
    }

    [Fact]
    public async Task Stale_version_returns_409_version_conflict()
    {
        var world = await SeedAsync();
        using var host = StartHost(world.DatabaseName);
        var tech = Client(host, TechEmail, world.TenantId);
        var created = await CreateAsync(tech, world.AssetId, Monday, Monday.AddHours(1), "Job");
        await UsageAsync(tech, created.Id, "start", created.Version);

        var stale = await PostUsageAsync(tech, created.Id, "work-finished", created.Version);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(PlanningException.VersionConflictCode, ReadCode(await stale.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Allocation_on_an_asset_that_is_not_plannable_returns_409_asset_not_plannable()
    {
        var world = await SeedAsync(policy: AssetHoursPolicy.WithinHours, weekdayHours: false);
        using var host = StartHost(world.DatabaseName);
        var tech = Client(host, TechEmail, world.TenantId);

        var response = await tech.PostAsJsonAsync(
            "/api/planning/allocations",
            CreateBody(world.AssetId, Monday, Monday.AddHours(1), "Job"),
            JsonOptions);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(PlanningException.AssetNotPlannableCode, ReadCode(await response.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Agenda_returns_resources_and_items_and_rejects_a_bad_range()
    {
        var world = await SeedAsync(policy: AssetHoursPolicy.WithinHours, weekdayHours: true);
        using var host = StartHost(world.DatabaseName);
        var tech = Client(host, TechEmail, world.TenantId);
        await CreateAsync(tech, world.AssetId, Monday, Monday.AddHours(2), "On the agenda");

        var agenda = await tech.GetFromJsonAsync<PlanningAgendaResponse>(AgendaPath(Monday.AddDays(-1)), JsonOptions);

        Assert.NotNull(agenda);
        Assert.Equal(2, agenda.Resources.Count);
        Assert.Equal(5, agenda.Resources.Single(r => r.AssetId == world.AssetId).WorkingHours.Count);
        Assert.Equal("On the agenda", Assert.Single(agenda.Items).Title);
        Assert.Empty(agenda.ExternalBusy);

        var bad = await tech.GetAsync($"/api/planning/agenda?from={Uri.EscapeDataString(Monday.ToString("O"))}&to={Uri.EscapeDataString(Monday.ToString("O"))}");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(PlanningException.InvalidRangeCode, ReadCode(await bad.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Other_tenant_data_is_invisible_and_untouchable_on_every_endpoint()
    {
        var world = await SeedAsync();
        using var host = StartHost(world.DatabaseName);
        var tech = Client(host, TechEmail, world.TenantId);
        var manager = Client(host, ManagerEmail, world.TenantId);
        var foreignId = world.ForeignAllocationId;

        // Read: get, list, agenda (default and asking for the foreign asset by id).
        var get = await tech.GetAsync($"/api/planning/allocations/{foreignId}");
        var getBody = await get.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(PlanningException.AllocationNotFoundCode, ReadCode(getBody));
        Assert.DoesNotContain(ForeignTitle, getBody, StringComparison.Ordinal);

        foreach (var path in new[]
        {
            "/api/planning/allocations",
            $"/api/planning/allocations?assetId={world.ForeignAssetId}",
            AgendaPath(Monday),
            AgendaPath(Monday) + $"&assetIds={world.ForeignAssetId}",
            AgendaPath(Monday) + $"&unitId={world.ForeignUnitId}",
        })
        {
            var body = await tech.GetStringAsync(path);
            Assert.DoesNotContain(ForeignTitle, body, StringComparison.Ordinal);
            Assert.DoesNotContain(world.ForeignAssetId.ToString(), body, StringComparison.Ordinal);
            Assert.DoesNotContain(foreignId.ToString(), body, StringComparison.Ordinal);
        }

        // Write: create and preview on a foreign asset, edit and drive a foreign allocation.
        var create = await tech.PostAsJsonAsync("/api/planning/allocations", CreateBody(world.ForeignAssetId, Monday, Monday.AddHours(1), "Smuggled"), JsonOptions);
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
        Assert.Equal(PlanningException.AssetNotFoundCode, ReadCode(await create.Content.ReadAsStringAsync()));
        var preview = await tech.PostAsJsonAsync("/api/planning/allocations/preview", CreateBody(world.ForeignAssetId, Monday, Monday.AddHours(1), "x"), JsonOptions);
        Assert.Equal(HttpStatusCode.NotFound, preview.StatusCode);

        var patch = await PatchAsync(manager, $"/api/planning/allocations/{foreignId}", new { expectedVersion = "AAAA", title = "Hijacked" });
        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
        foreach (var action in new[] { "start", "work-finished", "release", "cancel" })
        {
            var usage = await manager.PostAsJsonAsync($"/api/planning/allocations/{foreignId}/usage", new { action, expectedVersion = "AAAA" }, JsonOptions);
            Assert.Equal(HttpStatusCode.NotFound, usage.StatusCode);
        }

        // The foreign row is untouched and the foreign asset has no new allocation.
        await using var foreign = Verify(world.DatabaseName, world.ForeignTenantId);
        var row = await foreign.AssetAllocations.SingleAsync();
        Assert.Equal(ForeignTitle, row.Title);
        Assert.Equal(AllocationStatus.Scheduled, row.Status);
        await using var home = Verify(world.DatabaseName, world.TenantId);
        Assert.Empty(await home.AssetAllocations.ToListAsync());
    }

    [Fact]
    public async Task A_foreign_allocation_does_not_block_the_same_window_on_the_home_asset()
    {
        var world = await SeedAsync();
        using var host = StartHost(world.DatabaseName);
        var tech = Client(host, TechEmail, world.TenantId);

        // The foreign tenant holds Monday 12:00Z on its asset; the home asset is free then.
        var created = await CreateAsync(tech, world.AssetId, Monday, Monday.AddHours(1), "Home job");

        Assert.Equal("Scheduled", created.Status);
    }

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, string url, object body) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonContent.Create(body, options: JsonOptions) });

    private static string AgendaPath(DateTimeOffset around) =>
        $"/api/planning/agenda?from={Uri.EscapeDataString(around.AddDays(-1).ToString("O"))}&to={Uri.EscapeDataString(around.AddDays(7).ToString("O"))}";

    private static object CreateBody(Guid assetId, DateTimeOffset start, DateTimeOffset end, string title) => new
    {
        assetId,
        kind = "Use",
        title,
        workStart = start,
        workEnd = end,
    };

    private static async Task<AssetAllocationResponse> CreateAsync(
        HttpClient client,
        Guid assetId,
        DateTimeOffset start,
        DateTimeOffset end,
        string title,
        Guid? assetOverride = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/planning/allocations",
            CreateBody(assetOverride ?? assetId, start, end, title),
            JsonOptions);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonSerializer.Deserialize<AssetAllocationResponse>(text, JsonOptions)!;
    }

    private static Task<HttpResponseMessage> PostUsageAsync(
        HttpClient client,
        Guid id,
        string action,
        string version,
        string? reason = null) =>
        client.PostAsJsonAsync($"/api/planning/allocations/{id}/usage", new { action, expectedVersion = version, reason }, JsonOptions);

    private static async Task<AssetAllocationResponse> UsageAsync(
        HttpClient client,
        Guid id,
        string action,
        string version,
        string? reason = null)
    {
        var response = await PostUsageAsync(client, id, action, version, reason);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonSerializer.Deserialize<AssetAllocationResponse>(text, JsonOptions)!;
    }

    private static async Task<AssetAllocationResponse> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<AssetAllocationResponse>($"/api/planning/allocations/{id}", JsonOptions))!;

    private static string? ReadError(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("error").GetString();
    }

    private static string? ReadCode(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("code").GetString();
    }

    private static DateTimeOffset NextMonday(DateTimeOffset from)
    {
        var day = new DateTimeOffset(from.UtcDateTime.Date, TimeSpan.Zero).AddHours(12);
        while (day.DayOfWeek != DayOfWeek.Monday)
        {
            day = day.AddDays(1);
        }

        return day;
    }

    private static AppDbContext Verify(string databaseName, Guid tenantId) =>
        InMemoryAppDb.Create(new FakeTenantProvider { TenantId = tenantId }, databaseName);

    private static HttpClient Client(IHost host, string email, Guid tenantId)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, email);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, tenantId.ToString());
        return client;
    }

    private static async Task<World> SeedAsync(
        bool planningActive = true,
        AssetHoursPolicy policy = AssetHoursPolicy.Anytime,
        bool weekdayHours = false)
    {
        var databaseName = $"planning-alloc-{Guid.NewGuid():N}";
        var provider = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(provider, databaseName);

        var home = PlanningTestData.AddTenant(db, "Print Shop", policy, withPlanningModule: planningActive);
        var foreign = PlanningTestData.AddTenant(db, "Other Shop");
        if (weekdayHours)
        {
            foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday })
            {
                db.AssetWorkingHours.Add(PlanningTestData.Hours(home.TenantId, home.AssetId, day, 8, 18));
                db.AssetWorkingHours.Add(PlanningTestData.Hours(home.TenantId, home.SecondAssetId, day, 8, 18));
            }
        }

        var foreignAllocation = new AssetAllocation
        {
            TenantId = foreign.TenantId,
            AssetId = foreign.AssetId,
            UnitId = foreign.UnitId,
            Title = ForeignTitle,
            PlannedWorkStart = Monday,
            PlannedWorkEnd = Monday.AddHours(1),
            OccupiedStart = Monday,
            OccupiedEnd = Monday.AddHours(1),
            TimeZoneId = "America/Sao_Paulo",
        };
        db.AssetAllocations.Add(foreignAllocation);

        var techUserId = AddStaff(db, home.TenantId);
        await db.SaveChangesAsync();

        return new World(
            databaseName,
            home.TenantId,
            home.AssetId,
            home.SecondAssetId,
            techUserId,
            foreign.TenantId,
            foreign.AssetId,
            foreign.UnitId,
            foreignAllocation.Id);
    }

    private static Guid AddStaff(AppDbContext db, Guid tenantId)
    {
        var read = new Permission(Permissions.ResourcePlanning.AgendaRead, "Read", moduleKey: PlatformModules.ResourcePlanning);
        var manage = new Permission(Permissions.ResourcePlanning.AllocationsManage, "Manage", moduleKey: PlatformModules.ResourcePlanning);
        var execute = new Permission(Permissions.ResourcePlanning.AllocationsExecute, "Execute", moduleKey: PlatformModules.ResourcePlanning);
        db.Permissions.AddRange(read, manage, execute);

        var technicians = new Role(tenantId, "Planning technicians");
        var managers = new Role(tenantId, "Planning managers");
        var readers = new Role(tenantId, "Planning readers");
        db.Roles.AddRange(technicians, managers, readers);
        db.RolePermissions.AddRange(
            new RolePermission(technicians.Id, read.Id),
            new RolePermission(technicians.Id, execute.Id),
            new RolePermission(managers.Id, read.Id),
            new RolePermission(managers.Id, manage.Id),
            new RolePermission(managers.Id, execute.Id),
            new RolePermission(readers.Id, read.Id));

        var tech = new User(tenantId, TechEmail, "Tech One", TechEmail);
        var otherTech = new User(tenantId, OtherTechEmail, "Tech Two", OtherTechEmail);
        var manager = new User(tenantId, ManagerEmail, "Manager", ManagerEmail);
        var reader = new User(tenantId, ReaderEmail, "Reader", ReaderEmail);
        var noRole = new User(tenantId, NoRoleEmail, "No role", NoRoleEmail);
        db.Users.AddRange(tech, otherTech, manager, reader, noRole);
        db.UserRoles.AddRange(
            new UserRole(tech.Id, technicians.Id),
            new UserRole(otherTech.Id, technicians.Id),
            new UserRole(manager.Id, managers.Id),
            new UserRole(reader.Id, readers.Id));
        return tech.Id;
    }

    private static IHost StartHost(string databaseName) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddHttpContextAccessor();
                    services.AddSingleton(TimeProvider.System);
                    services.AddSingleton<IPlatformAdminChecker>(_ => new FakePlatformAdminChecker());
                    services.AddScoped<AmbientTenantContext>();
                    services.AddScoped<PlatformAdminTenantSession>();
                    services.AddScoped<ITenantProvider, HttpContextTenantProvider>();
                    services.AddScoped<AppDbContext>(sp =>
                        InMemoryAppDb.Create(sp.GetRequiredService<ITenantProvider>(), databaseName));
                    services.AddScoped<ITenantModuleAccessor, TenantModuleAccessor>();
                    services.AddScoped<IPermissionResolver, PermissionResolver>();
                    services.AddScoped<IRbacActorAccessor, RbacActorAccessor>();
                    services.AddScoped<IPlanningCallerAccessor, PlanningCallerAccessor>();
                    services.AddScoped<IAssetOccupancyService, AssetOccupancyService>();
                    services.AddScoped<IAssetAllocationService, AssetAllocationService>();
                    services.AddScoped<IPlanningAgendaService, PlanningAgendaService>();
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

                            manager.ApplicationParts.Add(new AssemblyPart(typeof(PlanningAllocationsController).Assembly));
                            manager.FeatureProviders.Add(new SingleController(typeof(PlanningAllocationsController)));
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

    private sealed record World(
        string DatabaseName,
        Guid TenantId,
        Guid AssetId,
        Guid SecondAssetId,
        Guid TechUserId,
        Guid ForeignTenantId,
        Guid ForeignAssetId,
        Guid ForeignUnitId,
        Guid ForeignAllocationId);

    private sealed class SingleController(Type controllerType) : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) => typeInfo.AsType() == controllerType;
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
