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
/// HTTP tests of <c>GET /api/planning/assets</c> and <c>GET/PUT .../profile</c>: permission gates,
/// module gate, tenant isolation (a second tenant with data of its own on every endpoint), the
/// profile rules and the atomic replacement of the weekly hours.
/// </summary>
public sealed class PlanningProfileHttpTests
{
    private const string WriterEmail = "planning-writer@shop.test";
    private const string ReaderEmail = "planning-reader@shop.test";
    private const string NoRoleEmail = "planning-norole@shop.test";
    private const string ForeignAssetName = "Foreign Secret Printer";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Controller_is_gated_by_resource_planning_and_each_action_by_its_permission()
    {
        Assert.Equal(
            PlatformModules.ResourcePlanning,
            typeof(PlanningAssetsController).GetCustomAttribute<RequireActiveModuleAttribute>()!.ModuleKey);

        Assert.Equal(PermissionPolicies.Name(Permissions.ResourcePlanning.AgendaRead), PolicyOf(nameof(PlanningAssetsController.List)));
        Assert.Equal(PermissionPolicies.Name(Permissions.ResourcePlanning.AgendaRead), PolicyOf(nameof(PlanningAssetsController.GetProfile)));
        Assert.Equal(PermissionPolicies.Name(Permissions.ResourcePlanning.AvailabilityWrite), PolicyOf(nameof(PlanningAssetsController.PutProfile)));

        static string PolicyOf(string action) =>
            typeof(PlanningAssetsController).GetMethod(action)!
                .GetCustomAttribute<RequirePermissionAttribute>()!.Policy!;
    }

    [Fact]
    public async Task Write_then_read_round_trips_the_profile_and_hours()
    {
        var world = await SeedAsync();
        using var host = StartHost(world.DatabaseName, allowPermissionDespiteModule: false);
        var writer = Client(host, WriterEmail, world.TenantId);
        var reader = Client(host, ReaderEmail, world.TenantId);

        var before = await reader.GetFromJsonAsync<AssetPlanningProfileResponse>(ProfilePath(world.AssetId), JsonOptions);
        Assert.NotNull(before);
        Assert.False(before.IsConfigured);
        Assert.False(before.IsPlannable);
        Assert.False(before.IsRentalBacked);
        Assert.Equal("StartWithinHours", before.HoursPolicy);
        Assert.Empty(before.WorkingHours);

        var saved = await PutAsync(writer, world.AssetId, Body(
            isPlannable: true,
            policy: "StartWithinHours",
            windows: [Window(1, "08:00", "12:00"), Window(1, "13:00", "18:00"), Window(2, "08:00", "18:00")],
            prep: 10,
            cleanup: 5));
        Assert.True(saved.IsConfigured);
        Assert.True(saved.IsPlannable);
        Assert.Equal(10, saved.DefaultPrepMinutes);
        Assert.Equal(5, saved.DefaultCleanupMinutes);
        Assert.Equal(3, saved.WorkingHours.Count);

        var after = await reader.GetFromJsonAsync<AssetPlanningProfileResponse>(ProfilePath(world.AssetId), JsonOptions);
        Assert.NotNull(after);
        Assert.True(after.IsPlannable);
        Assert.Equal(
            ["1 08:00-12:00", "1 13:00-18:00", "2 08:00-18:00"],
            after.WorkingHours.Select(w => $"{w.DayOfWeek} {w.OpenTime}-{w.CloseTime}").ToArray());

        await using var verify = Verify(world.DatabaseName, world.TenantId);
        var profile = await verify.AssetPlanningProfiles.SingleAsync();
        Assert.Equal(AssetHoursPolicy.StartWithinHours, profile.HoursPolicy);
        Assert.Equal(world.TenantId, profile.TenantId);
        Assert.Equal(3, await verify.AssetWorkingHours.CountAsync());
    }

    [Fact]
    public async Task Second_put_replaces_the_whole_weekly_hours_list_and_keeps_one_profile_row()
    {
        var world = await SeedAsync();
        using var host = StartHost(world.DatabaseName, allowPermissionDespiteModule: false);
        var writer = Client(host, WriterEmail, world.TenantId);

        await PutAsync(writer, world.AssetId, Body(true, "WithinHours", [Window(1, "08:00", "18:00"), Window(2, "08:00", "18:00")]));
        var second = await PutAsync(writer, world.AssetId, Body(true, "WithinHours", [Window(1, "08:00", "18:00"), Window(5, "09:00", "10:00")]));

        Assert.Equal(
            ["1 08:00-18:00", "5 09:00-10:00"],
            second.WorkingHours.Select(w => $"{w.DayOfWeek} {w.OpenTime}-{w.CloseTime}").ToArray());

        await using var verify = Verify(world.DatabaseName, world.TenantId);
        Assert.Equal(1, await verify.AssetPlanningProfiles.CountAsync());
        Assert.Equal(
            [1, 5],
            (await verify.AssetWorkingHours.Select(h => (int)h.DayOfWeek).ToListAsync()).Order().ToArray());
    }

    [Fact]
    public async Task Anytime_without_hours_is_accepted_and_a_policy_with_no_hours_is_rejected_when_plannable()
    {
        var world = await SeedAsync();
        using var host = StartHost(world.DatabaseName, allowPermissionDespiteModule: false);
        var writer = Client(host, WriterEmail, world.TenantId);

        var anytime = await PutAsync(writer, world.AssetId, Body(true, "Anytime", []));
        Assert.Equal("Anytime", anytime.HoursPolicy);
        Assert.Empty(anytime.WorkingHours);

        foreach (var policy in new[] { "StartWithinHours", "WithinHours" })
        {
            var rejected = await writer.PutAsJsonAsync(ProfilePath(world.AssetId), Body(true, policy, []), JsonOptions);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
            Assert.Equal(PlanningException.WorkingHoursRequiredCode, ReadCode(await rejected.Content.ReadAsStringAsync()));
        }

        // Not plannable: the profile is inert, so an empty week is saved as is.
        var inert = await PutAsync(writer, world.AssetId, Body(false, "StartWithinHours", []));
        Assert.False(inert.IsPlannable);
    }

    [Theory]
    [InlineData("Sometimes", 1, 0, 0)]
    [InlineData("Anytime", 0, 0, 0)]
    [InlineData("Anytime", 1, -1, 0)]
    [InlineData("Anytime", 1, 0, 1441)]
    public async Task Invalid_scalar_fields_are_rejected_with_400(string policy, int capacity, int prep, int cleanup)
    {
        var world = await SeedAsync();
        using var host = StartHost(world.DatabaseName, allowPermissionDespiteModule: false);
        var writer = Client(host, WriterEmail, world.TenantId);

        var response = await writer.PutAsJsonAsync(
            ProfilePath(world.AssetId),
            new
            {
                isPlannable = true,
                capacity,
                hoursPolicy = policy,
                defaultPrepMinutes = prep,
                defaultCleanupMinutes = cleanup,
                workingHours = Array.Empty<object>(),
            },
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PlanningException.InvalidProfileCode, ReadCode(await response.Content.ReadAsStringAsync()));
        await using var verify = Verify(world.DatabaseName, world.TenantId);
        Assert.Empty(await verify.AssetPlanningProfiles.ToListAsync());
    }

    [Theory]
    [InlineData(7, "08:00", "12:00")]
    [InlineData(-1, "08:00", "12:00")]
    [InlineData(1, "12:00", "08:00")]
    [InlineData(1, "08:00", "08:00")]
    [InlineData(1, "8am", "12:00")]
    public async Task Invalid_windows_are_rejected_with_400(int day, string open, string close)
    {
        var world = await SeedAsync();
        using var host = StartHost(world.DatabaseName, allowPermissionDespiteModule: false);
        var writer = Client(host, WriterEmail, world.TenantId);

        var response = await writer.PutAsJsonAsync(
            ProfilePath(world.AssetId),
            Body(true, "WithinHours", [Window(day, open, close)]),
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PlanningException.InvalidProfileCode, ReadCode(await response.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Overlapping_windows_on_the_same_day_are_rejected_but_touching_windows_are_not()
    {
        var world = await SeedAsync();
        using var host = StartHost(world.DatabaseName, allowPermissionDespiteModule: false);
        var writer = Client(host, WriterEmail, world.TenantId);

        var overlap = await writer.PutAsJsonAsync(
            ProfilePath(world.AssetId),
            Body(true, "WithinHours", [Window(1, "08:00", "12:00"), Window(1, "11:00", "15:00")]),
            JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, overlap.StatusCode);

        var touching = await PutAsync(
            writer,
            world.AssetId,
            Body(true, "WithinHours", [Window(1, "08:00", "12:00"), Window(1, "12:00", "15:00")]));
        Assert.Equal(2, touching.WorkingHours.Count);
    }

    [Fact]
    public async Task Rental_backed_asset_rejects_hours_with_409_but_accepts_a_profile_without_them()
    {
        var world = await SeedAsync();
        using var host = StartHost(world.DatabaseName, allowPermissionDespiteModule: false);
        var writer = Client(host, WriterEmail, world.TenantId);

        var rejected = await writer.PutAsJsonAsync(
            ProfilePath(world.RentalAssetId),
            Body(true, "WithinHours", [Window(1, "08:00", "18:00")]),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal(PlanningException.RentalScheduleAuthorityCode, ReadCode(await rejected.Content.ReadAsStringAsync()));

        var accepted = await PutAsync(writer, world.RentalAssetId, Body(true, "StartWithinHours", []));
        Assert.True(accepted.IsRentalBacked);
        Assert.True(accepted.IsPlannable);
        Assert.Empty(accepted.WorkingHours);

        await using var verify = Verify(world.DatabaseName, world.TenantId);
        Assert.Empty(await verify.AssetWorkingHours.Where(h => h.AssetId == world.RentalAssetId).ToListAsync());
    }

    [Fact]
    public async Task Rental_configuration_is_ignored_when_the_rentals_module_is_off()
    {
        var world = await SeedAsync(rentalsActive: false);
        using var host = StartHost(world.DatabaseName, allowPermissionDespiteModule: false);
        var writer = Client(host, WriterEmail, world.TenantId);

        var saved = await PutAsync(writer, world.RentalAssetId, Body(true, "WithinHours", [Window(1, "08:00", "18:00")]));

        Assert.False(saved.IsRentalBacked);
        Assert.Single(saved.WorkingHours);
    }

    [Fact]
    public async Task List_returns_tenant_assets_with_the_plannable_flag_and_filters()
    {
        var world = await SeedAsync();
        using var host = StartHost(world.DatabaseName, allowPermissionDespiteModule: false);
        var writer = Client(host, WriterEmail, world.TenantId);
        var reader = Client(host, ReaderEmail, world.TenantId);
        await PutAsync(writer, world.AssetId, Body(true, "WithinHours", [Window(1, "08:00", "18:00"), Window(2, "08:00", "18:00")]));

        var all = await reader.GetFromJsonAsync<List<PlanningAssetResponse>>("/api/planning/assets", JsonOptions);
        Assert.NotNull(all);
        Assert.Equal(["P1", "R1"], all.Select(a => a.Tag).ToArray());
        var printer = all.Single(a => a.Id == world.AssetId);
        Assert.True(printer.IsPlannable);
        Assert.False(printer.IsRentalBacked);
        Assert.Equal("WithinHours", printer.HoursPolicy);
        Assert.Equal(2, printer.WorkingWindowCount);
        Assert.Equal("Printers", printer.CategoryName);
        Assert.Equal("Matriz", printer.UnitName);
        var rental = all.Single(a => a.Id == world.RentalAssetId);
        Assert.False(rental.IsPlannable);
        Assert.True(rental.IsRentalBacked);
        Assert.Null(rental.HoursPolicy);

        var plannable = await reader.GetFromJsonAsync<List<PlanningAssetResponse>>("/api/planning/assets?plannable=true", JsonOptions);
        Assert.Equal([world.AssetId], plannable!.Select(a => a.Id).ToArray());

        var notPlannable = await reader.GetFromJsonAsync<List<PlanningAssetResponse>>("/api/planning/assets?plannable=false", JsonOptions);
        Assert.Equal([world.RentalAssetId], notPlannable!.Select(a => a.Id).ToArray());

        var searched = await reader.GetFromJsonAsync<List<PlanningAssetResponse>>("/api/planning/assets?search=PRINT", JsonOptions);
        Assert.Equal([world.AssetId], searched!.Select(a => a.Id).ToArray());

        var byUnit = await reader.GetFromJsonAsync<List<PlanningAssetResponse>>(
            $"/api/planning/assets?unitId={Guid.NewGuid()}",
            JsonOptions);
        Assert.Empty(byUnit!);

        var bad = await reader.GetAsync("/api/planning/assets?plannable=maybe");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(PlanningException.InvalidProfileCode, ReadCode(await bad.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Assets_scheduled_for_deletion_are_not_listed_and_have_no_profile()
    {
        var world = await SeedAsync();
        await using (var seed = Verify(world.DatabaseName, world.TenantId))
        {
            var asset = await seed.Assets.SingleAsync(a => a.Id == world.AssetId);
            asset.ScheduledDeletionAt = DateTimeOffset.UtcNow.AddDays(3);
            await seed.SaveChangesAsync();
        }

        using var host = StartHost(world.DatabaseName, allowPermissionDespiteModule: false);
        var reader = Client(host, ReaderEmail, world.TenantId);

        var all = await reader.GetFromJsonAsync<List<PlanningAssetResponse>>("/api/planning/assets", JsonOptions);
        Assert.DoesNotContain(all!, a => a.Id == world.AssetId);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync(ProfilePath(world.AssetId))).StatusCode);
    }

    [Fact]
    public async Task Other_tenant_assets_are_invisible_and_cannot_be_written_on_every_endpoint()
    {
        var world = await SeedAsync();
        await using (var seed = Verify(world.DatabaseName, world.ForeignTenantId))
        {
            seed.AssetPlanningProfiles.Add(new AssetPlanningProfile
            {
                TenantId = world.ForeignTenantId,
                AssetId = world.ForeignAssetId,
                IsPlannable = true,
                HoursPolicy = AssetHoursPolicy.Anytime,
            });
            await seed.SaveChangesAsync();
        }

        using var host = StartHost(world.DatabaseName, allowPermissionDespiteModule: false);
        var writer = Client(host, WriterEmail, world.TenantId);
        var reader = Client(host, ReaderEmail, world.TenantId);

        var list = await reader.GetAsync("/api/planning/assets");
        var listBody = await list.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.DoesNotContain(ForeignAssetName, listBody, StringComparison.Ordinal);
        Assert.DoesNotContain(world.ForeignAssetId.ToString(), listBody, StringComparison.Ordinal);

        var plannableOnly = await reader.GetStringAsync("/api/planning/assets?plannable=true");
        Assert.DoesNotContain(world.ForeignAssetId.ToString(), plannableOnly, StringComparison.Ordinal);

        var read = await reader.GetAsync(ProfilePath(world.ForeignAssetId));
        var readBody = await read.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(PlanningException.AssetNotFoundCode, ReadCode(readBody));
        Assert.DoesNotContain(ForeignAssetName, readBody, StringComparison.Ordinal);

        var write = await writer.PutAsJsonAsync(
            ProfilePath(world.ForeignAssetId),
            Body(true, "WithinHours", [Window(1, "08:00", "18:00")]),
            JsonOptions);
        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
        Assert.Equal(PlanningException.AssetNotFoundCode, ReadCode(await write.Content.ReadAsStringAsync()));

        // The foreign profile is untouched and nothing was created for either tenant.
        await using var foreign = Verify(world.DatabaseName, world.ForeignTenantId);
        var foreignProfile = await foreign.AssetPlanningProfiles.SingleAsync();
        Assert.Equal(AssetHoursPolicy.Anytime, foreignProfile.HoursPolicy);
        Assert.Empty(await foreign.AssetWorkingHours.ToListAsync());
        await using var home = Verify(world.DatabaseName, world.TenantId);
        Assert.Empty(await home.AssetPlanningProfiles.ToListAsync());
        Assert.Empty(await home.AssetWorkingHours.ToListAsync());
    }

    [Fact]
    public async Task Each_tenant_only_sees_its_own_planning_rows()
    {
        var world = await SeedAsync();
        await using (var seed = Verify(world.DatabaseName, world.ForeignTenantId))
        {
            seed.AssetPlanningProfiles.Add(new AssetPlanningProfile
            {
                TenantId = world.ForeignTenantId,
                AssetId = world.ForeignAssetId,
            });
            seed.AssetWorkingHours.Add(new AssetWorkingHours
            {
                TenantId = world.ForeignTenantId,
                AssetId = world.ForeignAssetId,
                DayOfWeek = 1,
                OpenTime = new TimeOnly(8, 0),
                CloseTime = new TimeOnly(18, 0),
            });
            seed.AssetAllocations.Add(new AssetAllocation
            {
                TenantId = world.ForeignTenantId,
                AssetId = world.ForeignAssetId,
                UnitId = world.ForeignUnitId,
                Title = "Foreign job",
                PlannedWorkStart = DateTimeOffset.UtcNow,
                PlannedWorkEnd = DateTimeOffset.UtcNow.AddHours(1),
                OccupiedStart = DateTimeOffset.UtcNow,
                OccupiedEnd = DateTimeOffset.UtcNow.AddHours(1),
                TimeZoneId = "America/Sao_Paulo",
            });
            await seed.SaveChangesAsync();
        }

        await using var home = Verify(world.DatabaseName, world.TenantId);
        Assert.Empty(await home.AssetPlanningProfiles.ToListAsync());
        Assert.Empty(await home.AssetWorkingHours.ToListAsync());
        Assert.Empty(await home.AssetAllocations.ToListAsync());

        await using var foreign = Verify(world.DatabaseName, world.ForeignTenantId);
        Assert.Single(await foreign.AssetPlanningProfiles.ToListAsync());
        Assert.Single(await foreign.AssetWorkingHours.ToListAsync());
        Assert.Single(await foreign.AssetAllocations.ToListAsync());
    }

    [Fact]
    public async Task Reader_cannot_write_and_a_user_without_planning_roles_cannot_read()
    {
        var world = await SeedAsync();
        using var host = StartHost(world.DatabaseName, allowPermissionDespiteModule: false);
        var reader = Client(host, ReaderEmail, world.TenantId);
        var noRole = Client(host, NoRoleEmail, world.TenantId);

        var write = await reader.PutAsJsonAsync(ProfilePath(world.AssetId), Body(true, "Anytime", []), JsonOptions);
        var writeBody = await write.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal(RbacErrorCodes.Forbidden, ReadError(writeBody));

        foreach (var path in new[] { "/api/planning/assets", ProfilePath(world.AssetId) })
        {
            var read = await noRole.GetAsync(path);
            var body = await read.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
            Assert.Equal(RbacErrorCodes.Forbidden, ReadError(body));
            Assert.DoesNotContain("Printer", body, StringComparison.Ordinal);
        }

        var noWrite = await noRole.PutAsJsonAsync(ProfilePath(world.AssetId), Body(true, "Anytime", []), JsonOptions);
        Assert.Equal(HttpStatusCode.Forbidden, noWrite.StatusCode);

        await using var verify = Verify(world.DatabaseName, world.TenantId);
        Assert.Empty(await verify.AssetPlanningProfiles.ToListAsync());
    }

    [Fact]
    public async Task Module_off_staff_role_is_forbidden_before_the_action()
    {
        var world = await SeedAsync(planningActive: false);
        using var host = StartHost(world.DatabaseName, allowPermissionDespiteModule: false);
        var writer = Client(host, WriterEmail, world.TenantId);

        foreach (var response in new[]
        {
            await writer.GetAsync("/api/planning/assets"),
            await writer.GetAsync(ProfilePath(world.AssetId)),
            await writer.PutAsJsonAsync(ProfilePath(world.AssetId), Body(true, "Anytime", []), JsonOptions),
        })
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(RbacErrorCodes.Forbidden, ReadError(body));
            Assert.DoesNotContain("Printer", body, StringComparison.Ordinal);
        }

        await using var verify = Verify(world.DatabaseName, world.TenantId);
        Assert.Empty(await verify.AssetPlanningProfiles.ToListAsync());
    }

    [Fact]
    public async Task Module_off_returns_module_inactive_when_the_caller_is_authorized()
    {
        var world = await SeedAsync(planningActive: false);
        using var host = StartHost(world.DatabaseName, allowPermissionDespiteModule: true);
        var client = Client(host, WriterEmail, world.TenantId);

        foreach (var response in new[]
        {
            await client.GetAsync("/api/planning/assets"),
            await client.GetAsync(ProfilePath(world.AssetId)),
            await client.PutAsJsonAsync(ProfilePath(world.AssetId), Body(true, "Anytime", []), JsonOptions),
        })
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var doc = JsonDocument.Parse(body);
            Assert.Equal(RequireActiveModuleAttribute.InactiveModuleError, doc.RootElement.GetProperty("error").GetString());
            Assert.Equal(RequireActiveModuleAttribute.InactiveModuleCode, doc.RootElement.GetProperty("code").GetString());
            Assert.Equal(PlatformModules.ResourcePlanning, doc.RootElement.GetProperty("module").GetString());
            Assert.DoesNotContain("Printer", body, StringComparison.Ordinal);
        }

        await using var verify = Verify(world.DatabaseName, world.TenantId);
        Assert.Empty(await verify.AssetPlanningProfiles.ToListAsync());
    }

    [Fact]
    public async Task Resource_planning_works_without_inventory_rentals_or_catalog()
    {
        var world = await SeedAsync(rentalsActive: false);
        await using (var seed = Verify(world.DatabaseName, world.TenantId))
        {
            var modules = await seed.TenantModules.Select(m => m.ModuleName).ToListAsync();
            Assert.Equal([PlatformModules.ResourcePlanning], modules);
        }

        using var host = StartHost(world.DatabaseName, allowPermissionDespiteModule: false);
        var writer = Client(host, WriterEmail, world.TenantId);

        var saved = await PutAsync(writer, world.AssetId, Body(true, "StartWithinHours", [Window(1, "08:00", "18:00")]));
        Assert.True(saved.IsPlannable);
    }

    private static string ProfilePath(Guid assetId) => $"/api/planning/assets/{assetId}/profile";

    private static object Window(int day, string open, string close) => new { dayOfWeek = day, openTime = open, closeTime = close };

    private static object Body(bool isPlannable, string policy, object[] windows, int prep = 0, int cleanup = 0) => new
    {
        isPlannable,
        capacity = 1,
        hoursPolicy = policy,
        defaultPrepMinutes = prep,
        defaultCleanupMinutes = cleanup,
        workingHours = windows,
    };

    private static async Task<AssetPlanningProfileResponse> PutAsync(HttpClient client, Guid assetId, object body)
    {
        var response = await client.PutAsJsonAsync(ProfilePath(assetId), body, JsonOptions);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonSerializer.Deserialize<AssetPlanningProfileResponse>(text, JsonOptions)!;
    }

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

    private static AppDbContext Verify(string databaseName, Guid tenantId) =>
        InMemoryAppDb.Create(new FakeTenantProvider { TenantId = tenantId }, databaseName);

    private static HttpClient Client(IHost host, string email, Guid tenantId)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, email);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, tenantId.ToString());
        return client;
    }

    private static async Task<World> SeedAsync(bool planningActive = true, bool rentalsActive = true)
    {
        var databaseName = $"planning-profile-{Guid.NewGuid():N}";
        var provider = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(provider, databaseName);

        var home = AddTenant(db, provider, "Print Shop", planningActive, rentalsActive, "Printer", "P1");
        var foreign = AddTenant(db, provider, "Other Shop", planningActive: true, rentalsActive: true, ForeignAssetName, "F1");
        AddStaff(db, home.TenantId);
        await db.SaveChangesAsync();

        return new World(
            databaseName,
            home.TenantId,
            home.AssetId,
            home.RentalAssetId,
            foreign.TenantId,
            foreign.AssetId,
            foreign.UnitId);
    }

    private static TenantSlice AddTenant(
        AppDbContext db,
        FakeTenantProvider provider,
        string legalName,
        bool planningActive,
        bool rentalsActive,
        string assetName,
        string tag)
    {
        var tenant = new Tenant(legalName, Guid.NewGuid().ToString("N")[..14], subdomain: $"plan-{Guid.NewGuid():N}"[..16]);
        provider.TenantId = tenant.Id;
        var unit = new Unit(tenant.Id, "Matriz");
        var category = new AssetCategory { TenantId = tenant.Id, Name = "Printers" };
        var family = new AssetFamily
        {
            Key = $"equip-{Guid.NewGuid():N}"[..32],
            Label = "Equipment",
            FieldSchemaJson = "{}",
        };
        var asset = new Asset
        {
            TenantId = tenant.Id,
            UnitId = unit.Id,
            CategoryId = category.Id,
            FamilyId = family.Id,
            Name = assetName,
            Tag = tag,
            Status = AssetStatus.Active,
            IsRentable = false,
        };
        var rentable = new Asset
        {
            TenantId = tenant.Id,
            UnitId = unit.Id,
            CategoryId = category.Id,
            FamilyId = family.Id,
            Name = "Court",
            Tag = "R1",
            Status = AssetStatus.Active,
            IsRentable = true,
        };
        var rental = new RentalAsset
        {
            TenantId = tenant.Id,
            AssetId = rentable.Id,
            Type = RentalAssetType.Good,
            TotalQuantity = 1,
            IsActive = true,
            RequiresDeposit = false,
            SchedulePolicy = SchedulePolicy.OpenHours,
        };

        db.Tenants.Add(tenant);
        db.Units.Add(unit);
        db.AssetCategories.Add(category);
        db.AssetFamilies.Add(family);
        db.Assets.AddRange(asset, rentable);
        db.RentalAssets.Add(rental);
        if (planningActive)
        {
            db.TenantModules.Add(new TenantModule(tenant.Id, PlatformModules.ResourcePlanning, isActive: true));
        }

        if (rentalsActive)
        {
            db.TenantModules.Add(new TenantModule(tenant.Id, PlatformModules.Rentals, isActive: true));
        }

        return new TenantSlice(tenant.Id, asset.Id, rentable.Id, unit.Id);
    }

    private static void AddStaff(AppDbContext db, Guid tenantId)
    {
        var read = new Permission(
            Permissions.ResourcePlanning.AgendaRead,
            "Read resource planning",
            moduleKey: PlatformModules.ResourcePlanning);
        var write = new Permission(
            Permissions.ResourcePlanning.AvailabilityWrite,
            "Write asset availability",
            moduleKey: PlatformModules.ResourcePlanning);
        var readers = new Role(tenantId, "Planning readers");
        var writers = new Role(tenantId, "Planning writers");
        var writer = new User(tenantId, WriterEmail, "Writer", WriterEmail);
        var reader = new User(tenantId, ReaderEmail, "Reader", ReaderEmail);
        var noRole = new User(tenantId, NoRoleEmail, "No role", NoRoleEmail);
        db.Permissions.AddRange(read, write);
        db.Roles.AddRange(readers, writers);
        db.Users.AddRange(writer, reader, noRole);
        db.RolePermissions.Add(new RolePermission(readers.Id, read.Id));
        db.RolePermissions.Add(new RolePermission(writers.Id, read.Id));
        db.RolePermissions.Add(new RolePermission(writers.Id, write.Id));
        db.UserRoles.Add(new UserRole(reader.Id, readers.Id));
        db.UserRoles.Add(new UserRole(writer.Id, writers.Id));
    }

    private static IHost StartHost(string databaseName, bool allowPermissionDespiteModule) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddHttpContextAccessor();
                    services.AddSingleton<IPlatformAdminChecker>(_ => new FakePlatformAdminChecker());
                    services.AddScoped<AmbientTenantContext>();
                    services.AddScoped<PlatformAdminTenantSession>();
                    services.AddScoped<ITenantProvider, HttpContextTenantProvider>();
                    services.AddScoped<AppDbContext>(sp =>
                        InMemoryAppDb.Create(sp.GetRequiredService<ITenantProvider>(), databaseName));
                    services.AddScoped<ITenantModuleAccessor, TenantModuleAccessor>();
                    services.AddScoped<IPermissionResolver, PermissionResolver>();
                    services.AddScoped<IPlanningProfileService, PlanningProfileService>();
                    if (allowPermissionDespiteModule)
                    {
                        services.AddSingleton<IAuthorizationHandler>(
                            new AllowPermissionHandler(
                                Permissions.ResourcePlanning.AgendaRead,
                                Permissions.ResourcePlanning.AvailabilityWrite));
                    }
                    else
                    {
                        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
                    }

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

                            manager.ApplicationParts.Add(new AssemblyPart(typeof(PlanningAssetsController).Assembly));
                            manager.FeatureProviders.Add(new SingleController(typeof(PlanningAssetsController)));
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
        Guid RentalAssetId,
        Guid ForeignTenantId,
        Guid ForeignAssetId,
        Guid ForeignUnitId);

    private sealed record TenantSlice(Guid TenantId, Guid AssetId, Guid RentalAssetId, Guid UnitId);

    private sealed class SingleController(Type controllerType) : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) => typeInfo.AsType() == controllerType;
    }

    private sealed class AllowPermissionHandler(params string[] permissionKeys) : AuthorizationHandler<PermissionRequirement>
    {
        protected override Task HandleRequirementAsync(
            AuthorizationHandlerContext context,
            PermissionRequirement requirement)
        {
            if (permissionKeys.Any(key => requirement.PermissionKeys.Contains(key)))
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
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
            }

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
