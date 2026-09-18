using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Authorization;
using Platform.Api.Modules.Pmoc;
using Platform.Api.Modules.Pmoc.Controllers;
using Platform.Api.Modules.Pmoc.Dtos;
using Platform.Api.Modules.Pmoc.Services;
using Platform.Api.Tests.Assets;
using Platform.Core.Domain.Common;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence.Seed;

namespace Platform.Api.Tests.Pmoc;

public sealed class PmocPhase1TemplateLibraryTests
{
    [Fact]
    public async Task Published_templates_appear_in_library_list_and_deprecated_do_not()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var templates = CreateTemplateService(harness);
        await EnsureAnvisaSeedAsync(harness);
        var deprecated = await InsertTemplateAsync(
            harness,
            name: "Deprecated NR-10",
            libraryKey: "pmoc-deprecated-nr10",
            version: 1,
            status: GlobalTemplateStatus.Deprecated,
            jurisdiction: "BR",
            tasks: [TaskSpec("Old checklist", TaskInputType.Checkbox, 1)]);

        var listed = await templates.ListAsync(jurisdiction: null, CancellationToken.None);

        Assert.Contains(listed, item => item.Id == GlobalTemplateSeed.AnvisaNr10TemplateId);
        Assert.DoesNotContain(listed, item => item.Id == deprecated.Id);
        Assert.All(listed, item => Assert.Equal(GlobalTemplateStatus.Published, item.Status));
    }

    [Fact]
    public async Task Get_by_id_returns_library_fields_and_tasks_including_deprecated()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var templates = CreateTemplateService(harness);
        await EnsureAnvisaSeedAsync(harness);
        var deprecated = await InsertTemplateAsync(
            harness,
            name: "Old edition",
            libraryKey: "pmoc-old-edition",
            version: 1,
            status: GlobalTemplateStatus.Deprecated,
            jurisdiction: "BR",
            sourceReferences: "Lei 13.589/2018 (old)",
            tasks:
            [
                TaskSpec("Foto", TaskInputType.Image, 1),
                TaskSpec("Corrente", TaskInputType.Number, 2, """{"unit":"A"}""", isMandatory: false),
            ]);

        var published = await templates.GetByIdAsync(
            GlobalTemplateSeed.AnvisaNr10TemplateId,
            CancellationToken.None);
        Assert.NotNull(published);
        Assert.Equal(GlobalTemplateSeed.AnvisaLibraryKey, published!.LibraryKey);
        Assert.Equal(GlobalTemplateSeed.AnvisaVersion, published.Version);
        Assert.Equal(GlobalTemplateStatus.Published, published.Status);
        Assert.Equal(GlobalTemplateSeed.AnvisaSourceReferences, published.SourceReferences);
        Assert.Equal(6, published.Tasks.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6], published.Tasks.Select(task => task.Order).ToArray());

        var preview = await templates.GetByIdAsync(deprecated.Id, CancellationToken.None);
        Assert.NotNull(preview);
        Assert.Equal(GlobalTemplateStatus.Deprecated, preview!.Status);
        Assert.Equal("pmoc-old-edition", preview.LibraryKey);
        Assert.Equal("Lei 13.589/2018 (old)", preview.SourceReferences);
        Assert.Equal(["Foto", "Corrente"], preview.Tasks.Select(task => task.Title).ToArray());
        Assert.False(preview.Tasks.Single(task => task.Title == "Corrente").IsMandatory);
        Assert.Equal("""{"unit":"A"}""", preview.Tasks.Single(task => task.Title == "Corrente").Configuration);
    }

    [Fact]
    public async Task Get_by_id_missing_template_is_404()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var templates = CreateTemplateService(harness);
        var controller = new GlobalTemplatesController(templates);
        var missingId = Guid.NewGuid();

        Assert.Null(await templates.GetByIdAsync(missingId, CancellationToken.None));
        var result = await controller.GetById(missingId, CancellationToken.None);
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task From_template_stamps_rolvix_lineage_and_auto_generate_false_when_omitted()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var (plans, templates) = CreateServices(harness);
        var seed = await EnsureAnvisaSeedAsync(harness);

        var created = await plans.CreateFromTemplateAsync(
            FromTemplate(harness, seed.Id),
            CancellationToken.None);

        Assert.Equal(MaintenancePlanOriginKind.RolvixTemplate, created.OriginKind);
        Assert.Equal(seed.Id, created.SourceTemplateId);
        Assert.Equal(seed.Version, created.SourceTemplateVersion);
        Assert.False(created.AutoGenerateEnabled);
        Assert.True(created.IsActive);
        Assert.Equal(seed.Name, created.Name);
        Assert.Equal(seed.Description, created.Description);
        Assert.Equal(seed.Frequency, created.Frequency);

        var stored = await harness.Db.MaintenancePlans.SingleAsync(plan => plan.Id == created.Id);
        Assert.Equal(MaintenancePlanOriginKind.RolvixTemplate, stored.OriginKind);
        Assert.Equal(seed.Id, stored.SourceTemplateId);
        Assert.Equal(seed.Version, stored.SourceTemplateVersion);
        Assert.False(stored.AutoGenerateEnabled);

        var preview = await templates.GetByIdAsync(seed.Id, CancellationToken.None);
        Assert.Equal(preview!.Version, created.SourceTemplateVersion);
    }

    [Fact]
    public async Task From_template_copies_all_tasks_with_new_ids()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var (plans, templates) = CreateServices(harness);
        await EnsureAnvisaSeedAsync(harness);
        var source = await templates.GetByIdAsync(
            GlobalTemplateSeed.AnvisaNr10TemplateId,
            CancellationToken.None);

        var created = await plans.CreateFromTemplateAsync(
            FromTemplate(harness, source!.Id, name: "Cloned PMOC"),
            CancellationToken.None);

        Assert.Equal(source.Tasks.Count, created.Tasks.Count);
        var sourceIds = source.Tasks.Select(task => task.Id).ToHashSet();
        Assert.Equal(
            source.Tasks.Select(task => (task.Title, task.InputType, task.Configuration, task.IsMandatory, task.Order)),
            created.Tasks.Select(task => (task.Title, task.InputType, task.Configuration, task.IsMandatory, task.Order)));
        Assert.All(created.Tasks, task => Assert.DoesNotContain(task.Id, sourceIds));
        Assert.DoesNotContain(GlobalTemplateSeed.TaskPhotoLocalId, created.Tasks.Select(task => task.Id));
        Assert.DoesNotContain(GlobalTemplateSeed.TaskCurrentMeasureId, created.Tasks.Select(task => task.Id));
        Assert.All(created.Tasks, task => Assert.Equal(created.Id, task.MaintenancePlanId));
        Assert.All(created.Tasks, task => Assert.Equal(created.TenantId, task.TenantId));
    }

    [Fact]
    public async Task Mutating_template_after_clone_leaves_plan_and_tasks_unchanged()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var (plans, templates) = CreateServices(harness);
        var seed = await EnsureAnvisaSeedAsync(harness);
        var created = await plans.CreateFromTemplateAsync(
            FromTemplate(harness, seed.Id),
            CancellationToken.None);
        var snapshot = created.Tasks
            .Select(task => (task.Id, task.Title, task.InputType, task.Configuration, task.IsMandatory, task.Order))
            .ToArray();

        var template = await harness.Db.GlobalMaintenanceTemplates
            .Include(item => item.Tasks)
            .SingleAsync(item => item.Id == seed.Id);
        template.Name = "MUTATED LIBRARY NAME";
        template.Status = GlobalTemplateStatus.Deprecated;
        template.SourceReferences = "mutated refs";
        foreach (var task in template.Tasks)
        {
            task.Title = "MUTATED " + task.Title;
        }

        template.AddTask(
            new GlobalTemplateTask
            {
                GlobalMaintenanceTemplateId = template.Id,
                Title = "Only on library after clone",
                InputType = TaskInputType.Text,
                IsMandatory = false,
                Order = 99,
            });
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var reloaded = await plans.GetByIdAsync(created.Id, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(created.Name, reloaded!.Name);
        Assert.Equal(created.Description, reloaded.Description);
        Assert.Equal(MaintenancePlanOriginKind.RolvixTemplate, reloaded.OriginKind);
        Assert.Equal(seed.Id, reloaded.SourceTemplateId);
        Assert.Equal(seed.Version, reloaded.SourceTemplateVersion);
        Assert.Equal(snapshot, reloaded.Tasks.Select(task =>
            (task.Id, task.Title, task.InputType, task.Configuration, task.IsMandatory, task.Order)).ToArray());
        Assert.DoesNotContain(reloaded.Tasks, task => task.Title.StartsWith("MUTATED ", StringComparison.Ordinal));
        Assert.DoesNotContain(reloaded.Tasks, task => task.Title == "Only on library after clone");

        var library = await templates.ListAsync(jurisdiction: null, CancellationToken.None);
        Assert.DoesNotContain(library, item => item.Id == seed.Id);
        var preview = await templates.GetByIdAsync(seed.Id, CancellationToken.None);
        Assert.Equal(GlobalTemplateStatus.Deprecated, preview!.Status);
        Assert.Equal("MUTATED LIBRARY NAME", preview.Name);
    }

    [Fact]
    public async Task Generic_create_cannot_spoof_library_provenance()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plans = CreatePlanService(harness);

        Assert.Null(typeof(CreateMaintenancePlanRequest).GetProperty(nameof(MaintenancePlan.OriginKind)));
        Assert.Null(typeof(CreateMaintenancePlanRequest).GetProperty(nameof(MaintenancePlan.SourceTemplateId)));
        Assert.Null(typeof(CreateMaintenancePlanRequest).GetProperty(nameof(MaintenancePlan.SourceTemplateVersion)));

        var created = await plans.CreatePlanWithTasksAsync(
            new CreateMaintenancePlanRequest
            {
                UnitId = harness.UnitId,
                Name = "Custom PMOC",
                Frequency = MaintenanceFrequency.Monthly,
                AssetCategoryId = harness.CategoryId,
                Tasks =
                [
                    new CreatePlanTaskDto
                    {
                        Title = "Filtro",
                        InputType = TaskInputType.Checkbox,
                        Order = 1,
                    },
                ],
            },
            CancellationToken.None);

        Assert.Equal(MaintenancePlanOriginKind.Custom, created.OriginKind);
        Assert.Null(created.SourceTemplateId);
        Assert.Null(created.SourceTemplateVersion);
        Assert.False(created.AutoGenerateEnabled);
        var stored = await harness.Db.MaintenancePlans.SingleAsync(plan => plan.Id == created.Id);
        Assert.Equal(MaintenancePlanOriginKind.Custom, stored.OriginKind);
        Assert.Null(stored.SourceTemplateId);
        Assert.Null(stored.SourceTemplateVersion);
    }

    [Fact]
    public async Task Header_update_of_cloned_plan_cannot_overwrite_origin()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plans = CreatePlanService(harness);
        var seed = await EnsureAnvisaSeedAsync(harness);
        var created = await plans.CreateFromTemplateAsync(
            FromTemplate(harness, seed.Id),
            CancellationToken.None);

        var updated = await plans.UpdateAsync(
            created.Id,
            new UpdateMaintenancePlanRequest
            {
                UnitId = harness.UnitId,
                Name = "Renamed clone",
                Description = "Still cloned",
                Frequency = MaintenanceFrequency.Weekly,
                AssetCategoryId = harness.CategoryId,
                IsActive = false,
                AutoGenerateEnabled = true,
            },
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("Renamed clone", updated!.Name);
        Assert.False(updated.IsActive);
        Assert.True(updated.AutoGenerateEnabled);
        Assert.Equal(MaintenancePlanOriginKind.RolvixTemplate, updated.OriginKind);
        Assert.Equal(seed.Id, updated.SourceTemplateId);
        Assert.Equal(seed.Version, updated.SourceTemplateVersion);
        var stored = await harness.Db.MaintenancePlans.SingleAsync(plan => plan.Id == created.Id);
        Assert.Equal(MaintenancePlanOriginKind.RolvixTemplate, stored.OriginKind);
        Assert.Equal(seed.Id, stored.SourceTemplateId);
        Assert.Equal(seed.Version, stored.SourceTemplateVersion);
    }

    [Fact]
    public async Task Cross_tenant_cannot_access_cloned_plan()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var (plans, templates) = CreateServices(harness);
        var seed = await EnsureAnvisaSeedAsync(harness);
        var created = await plans.CreateFromTemplateAsync(
            FromTemplate(harness, seed.Id, name: "Tenant-A clone"),
            CancellationToken.None);
        var originalTenant = harness.TenantProvider.TenantId;
        var tenantB = new Tenant("Tenant B", "88888888000192", subdomain: "pmoc-library-b");
        harness.Db.Tenants.Add(tenantB);
        await harness.Db.SaveChangesAsync();
        harness.TenantProvider.TenantId = tenantB.Id;

        var library = await templates.ListAsync(jurisdiction: null, CancellationToken.None);
        Assert.Contains(library, item => item.Id == seed.Id);
        Assert.NotNull(await templates.GetByIdAsync(seed.Id, CancellationToken.None));

        Assert.Null(await plans.GetByIdAsync(created.Id, CancellationToken.None));
        Assert.DoesNotContain(
            await plans.ListAsync(CancellationToken.None),
            plan => plan.Id == created.Id);
        Assert.Null(
            await plans.UpdateAsync(
                created.Id,
                new UpdateMaintenancePlanRequest
                {
                    UnitId = harness.UnitId,
                    Name = "Hacked",
                    Frequency = MaintenanceFrequency.Monthly,
                    AssetCategoryId = harness.CategoryId,
                    IsActive = false,
                    AutoGenerateEnabled = true,
                },
                CancellationToken.None));
        Assert.Null(
            await plans.ReplaceTasksAsync(
                created.Id,
                new ReplacePlanTasksRequest
                {
                    Tasks =
                    [
                        new ReplacePlanTaskDto
                        {
                            Title = "Hacked",
                            InputType = TaskInputType.Checkbox,
                            Order = 1,
                        },
                    ],
                },
                CancellationToken.None));
        Assert.False(await plans.DeleteAsync(created.Id, CancellationToken.None));

        harness.TenantProvider.TenantId = originalTenant;
        var stillThere = await plans.GetByIdAsync(created.Id, CancellationToken.None);
        Assert.NotNull(stillThere);
        Assert.Equal("Tenant-A clone", stillThere!.Name);
        Assert.Equal(MaintenancePlanOriginKind.RolvixTemplate, stillThere.OriginKind);
        Assert.Equal(seed.Id, stillThere.SourceTemplateId);
        Assert.Equal(6, stillThere.Tasks.Count);
    }

    [Fact]
    public void From_template_requires_plans_write()
    {
        var method = typeof(MaintenancePlansController).GetMethod(
            nameof(MaintenancePlansController.CreateFromTemplate));
        Assert.NotNull(method);
        var permission = method!.GetCustomAttribute<RequirePermissionAttribute>();
        Assert.NotNull(permission);
        Assert.Equal(PermissionPolicies.Name(Permissions.Pmoc.PlansWrite), permission!.Policy);
        var httpPost = method.GetCustomAttribute<HttpPostAttribute>();
        Assert.NotNull(httpPost);
        Assert.Equal("from-template", httpPost!.Template);
        Assert.Equal(
            PlatformModules.Pmoc,
            typeof(MaintenancePlansController).GetCustomAttribute<RequireActiveModuleAttribute>()!.ModuleKey);
    }

    [Fact]
    public void Get_template_by_id_requires_templates_read()
    {
        var method = typeof(GlobalTemplatesController).GetMethod(nameof(GlobalTemplatesController.GetById));
        Assert.NotNull(method);
        var permission = method!.GetCustomAttribute<RequirePermissionAttribute>();
        Assert.NotNull(permission);
        Assert.Equal(PermissionPolicies.Name(Permissions.Pmoc.TemplatesRead), permission!.Policy);
        var httpGet = method.GetCustomAttribute<HttpGetAttribute>();
        Assert.NotNull(httpGet);
        Assert.Equal("{id:guid}", httpGet!.Template);
        Assert.Equal(
            PlatformModules.Pmoc,
            typeof(GlobalTemplatesController).GetCustomAttribute<RequireActiveModuleAttribute>()!.ModuleKey);

        var list = typeof(GlobalTemplatesController).GetMethod(nameof(GlobalTemplatesController.List));
        Assert.Equal(
            PermissionPolicies.Name(Permissions.Pmoc.TemplatesRead),
            list!.GetCustomAttribute<RequirePermissionAttribute>()!.Policy);
    }

    [Fact]
    public void Seed_guid_and_metadata_constants_are_unchanged()
    {
        Assert.Equal(
            Guid.Parse("6f1c2a0e-4b9d-4f3a-9c7e-1d2a3b4c5d6e"),
            GlobalTemplateSeed.AnvisaNr10TemplateId);
        Assert.Equal("pmoc-ar-condicionado-anvisa-nr10", GlobalTemplateSeed.AnvisaLibraryKey);
        Assert.Equal(1, GlobalTemplateSeed.AnvisaVersion);
        Assert.Equal(GlobalTemplateStatus.Published, GlobalTemplateSeed.AnvisaStatus);
        Assert.Equal(
            "Lei 13.589/2018; Resolução Anvisa RE 09; NR-10",
            GlobalTemplateSeed.AnvisaSourceReferences);
        Assert.DoesNotContain("CREA", GlobalTemplateSeed.AnvisaSourceReferences, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Jurisdiction_filter_keeps_br_and_requested_published_only()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var templates = CreateTemplateService(harness);
        await EnsureAnvisaSeedAsync(harness);
        var spPublished = await InsertTemplateAsync(
            harness,
            name: "SP Published",
            libraryKey: "pmoc-sp-published",
            version: 1,
            status: GlobalTemplateStatus.Published,
            jurisdiction: "SP",
            tasks: [TaskSpec("SP task", TaskInputType.Checkbox, 1)]);
        var brDeprecated = await InsertTemplateAsync(
            harness,
            name: "BR Deprecated",
            libraryKey: "pmoc-br-deprecated",
            version: 1,
            status: GlobalTemplateStatus.Deprecated,
            jurisdiction: "BR",
            tasks: [TaskSpec("Hidden", TaskInputType.Checkbox, 1)]);
        var rjDeprecated = await InsertTemplateAsync(
            harness,
            name: "RJ Deprecated",
            libraryKey: "pmoc-rj-deprecated",
            version: 1,
            status: GlobalTemplateStatus.Deprecated,
            jurisdiction: "RJ",
            tasks: [TaskSpec("Hidden RJ", TaskInputType.Checkbox, 1)]);

        var all = await templates.ListAsync(jurisdiction: null, CancellationToken.None);
        Assert.Contains(all, item => item.Id == GlobalTemplateSeed.AnvisaNr10TemplateId);
        Assert.Contains(all, item => item.Id == spPublished.Id);
        Assert.DoesNotContain(all, item => item.Id == brDeprecated.Id);
        Assert.DoesNotContain(all, item => item.Id == rjDeprecated.Id);

        var sp = await templates.ListAsync("SP", CancellationToken.None);
        Assert.Contains(sp, item => item.Id == GlobalTemplateSeed.AnvisaNr10TemplateId);
        Assert.Contains(sp, item => item.Id == spPublished.Id);
        Assert.DoesNotContain(sp, item => item.Id == brDeprecated.Id);

        var rj = await templates.ListAsync("RJ", CancellationToken.None);
        Assert.Contains(rj, item => item.Id == GlobalTemplateSeed.AnvisaNr10TemplateId);
        Assert.DoesNotContain(rj, item => item.Id == spPublished.Id);
        Assert.DoesNotContain(rj, item => item.Id == rjDeprecated.Id);
    }

    [Fact]
    public async Task From_template_missing_template_is_404()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plans = CreatePlanService(harness);
        var missingId = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => plans.CreateFromTemplateAsync(FromTemplate(harness, missingId), CancellationToken.None));

        Assert.Contains(missingId.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.False(await harness.Db.MaintenancePlans.AnyAsync());
    }

    [Fact]
    public async Task From_template_deprecated_template_is_400()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plans = CreatePlanService(harness);
        var deprecated = await InsertTemplateAsync(
            harness,
            name: "Cannot clone",
            libraryKey: "pmoc-cannot-clone",
            version: 1,
            status: GlobalTemplateStatus.Deprecated,
            jurisdiction: "BR",
            tasks: [TaskSpec("Old", TaskInputType.Checkbox, 1)]);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => plans.CreateFromTemplateAsync(FromTemplate(harness, deprecated.Id), CancellationToken.None));

        Assert.Contains("published", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await harness.Db.MaintenancePlans.AnyAsync());
    }

    [Fact]
    public async Task Custom_create_remains_custom_with_auto_generate_false()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plans = CreatePlanService(harness);

        var created = await plans.CreatePlanWithTasksAsync(
            new CreateMaintenancePlanRequest
            {
                UnitId = harness.UnitId,
                Name = "Personalizado",
                Frequency = MaintenanceFrequency.Monthly,
                AssetCategoryId = harness.CategoryId,
                Tasks =
                [
                    new CreatePlanTaskDto
                    {
                        Title = "Filtro",
                        InputType = TaskInputType.Checkbox,
                        Order = 1,
                    },
                ],
            },
            CancellationToken.None);

        Assert.Equal(MaintenancePlanOriginKind.Custom, created.OriginKind);
        Assert.Null(created.SourceTemplateId);
        Assert.Null(created.SourceTemplateVersion);
        Assert.False(created.AutoGenerateEnabled);
    }

    [Fact]
    public async Task Cloning_selected_version_does_not_copy_other_version_tasks()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plans = CreatePlanService(harness);
        var v1 = await EnsureAnvisaSeedAsync(harness);
        var v2 = await InsertTemplateAsync(
            harness,
            name: "ANVISA v2",
            libraryKey: GlobalTemplateSeed.AnvisaLibraryKey,
            version: 2,
            status: GlobalTemplateStatus.Published,
            jurisdiction: "BR",
            tasks:
            [
                TaskSpec("Only v2 task A", TaskInputType.Checkbox, 1),
                TaskSpec("Only v2 task B", TaskInputType.Text, 2),
            ]);

        var clonedV1 = await plans.CreateFromTemplateAsync(
            FromTemplate(harness, v1.Id),
            CancellationToken.None);
        Assert.Equal(v1.Id, clonedV1.SourceTemplateId);
        Assert.Equal(1, clonedV1.SourceTemplateVersion);
        Assert.Equal(6, clonedV1.Tasks.Count);
        Assert.DoesNotContain(clonedV1.Tasks, task => task.Title.StartsWith("Only v2", StringComparison.Ordinal));
        Assert.Contains(
            clonedV1.Tasks,
            task => task.Title == "Foto do Local de Instalação (Visão Geral do Ambiente)");

        var clonedV2 = await plans.CreateFromTemplateAsync(
            FromTemplate(harness, v2.Id),
            CancellationToken.None);
        Assert.Equal(v2.Id, clonedV2.SourceTemplateId);
        Assert.Equal(2, clonedV2.SourceTemplateVersion);
        Assert.Equal(
            ["Only v2 task A", "Only v2 task B"],
            clonedV2.Tasks.Select(task => task.Title).ToArray());
        Assert.DoesNotContain(
            clonedV2.Tasks,
            task => task.Title == "Foto do Local de Instalação (Visão Geral do Ambiente)");
    }

    [Fact]
    public async Task From_template_honors_explicit_auto_generate_true()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plans = CreatePlanService(harness);
        var seed = await EnsureAnvisaSeedAsync(harness);

        var created = await plans.CreateFromTemplateAsync(
            FromTemplate(harness, seed.Id, autoGenerateEnabled: true),
            CancellationToken.None);

        Assert.True(created.AutoGenerateEnabled);
        Assert.Equal(MaintenancePlanOriginKind.RolvixTemplate, created.OriginKind);
        var stored = await harness.Db.MaintenancePlans.SingleAsync(plan => plan.Id == created.Id);
        Assert.True(stored.AutoGenerateEnabled);
    }

    [Fact]
    public async Task Controller_from_template_returns_201_created_at_get_by_id()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plans = CreatePlanService(harness);
        var seed = await EnsureAnvisaSeedAsync(harness);
        var controller = new MaintenancePlansController(plans, harness.CreateRegistry());

        var result = await controller.CreateFromTemplate(
            FromTemplate(harness, seed.Id, name: "HTTP clone"),
            CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        Assert.Equal(nameof(MaintenancePlansController.GetById), created.ActionName);
        var body = Assert.IsType<MaintenancePlanResponse>(created.Value);
        Assert.Equal("HTTP clone", body.Name);
        Assert.Equal(MaintenancePlanOriginKind.RolvixTemplate, body.OriginKind);
        Assert.Equal(seed.Id, body.SourceTemplateId);
        Assert.Equal(body.Id, created.RouteValues!["id"]);
    }

    [Fact]
    public async Task Controller_from_template_maps_deprecated_to_400()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plans = CreatePlanService(harness);
        var deprecated = await InsertTemplateAsync(
            harness,
            name: "HTTP deprecated",
            libraryKey: "pmoc-http-deprecated",
            version: 1,
            status: GlobalTemplateStatus.Deprecated,
            jurisdiction: "BR",
            tasks: [TaskSpec("Old", TaskInputType.Checkbox, 1)]);
        var controller = new MaintenancePlansController(plans, harness.CreateRegistry());

        var result = await controller.CreateFromTemplate(
            FromTemplate(harness, deprecated.Id),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(badRequest.Value));
        Assert.Contains(
            "published",
            doc.RootElement.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.False(await harness.Db.MaintenancePlans.AnyAsync());
    }

    [Fact]
    public async Task Controller_from_template_maps_missing_to_404()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plans = CreatePlanService(harness);
        var missingId = Guid.NewGuid();
        var controller = new MaintenancePlansController(plans, harness.CreateRegistry());

        var result = await controller.CreateFromTemplate(
            FromTemplate(harness, missingId),
            CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(notFound.Value));
        Assert.Contains(missingId.ToString(), doc.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Create_from_template_request_has_no_client_task_list()
    {
        Assert.Null(typeof(CreateFromTemplateRequest).GetProperty("Tasks"));
        Assert.Null(typeof(CreateFromTemplateRequest).GetProperty("OriginKind"));
        Assert.Null(typeof(CreateFromTemplateRequest).GetProperty("SourceTemplateId"));
        Assert.Null(typeof(CreateFromTemplateRequest).GetProperty("SourceTemplateVersion"));
        Assert.Null(typeof(CreateFromTemplateRequest).GetProperty("Frequency"));
    }

    [Fact]
    public async Task From_template_name_override_and_inactive_flag()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plans = CreatePlanService(harness);
        var seed = await EnsureAnvisaSeedAsync(harness);

        var created = await plans.CreateFromTemplateAsync(
            new CreateFromTemplateRequest
            {
                TemplateId = seed.Id,
                UnitId = harness.UnitId,
                AssetCategoryId = harness.CategoryId,
                Name = "  Override name  ",
                Description = "  Override description  ",
                IsActive = false,
            },
            CancellationToken.None);

        Assert.Equal("Override name", created.Name);
        Assert.Equal("Override description", created.Description);
        Assert.False(created.IsActive);
        Assert.False(created.AutoGenerateEnabled);
        Assert.Equal(MaintenancePlanOriginKind.RolvixTemplate, created.OriginKind);
    }

    private static (MaintenancePlanService Plans, GlobalTemplateService Templates) CreateServices(
        BulkCreateAssetsHarness harness) =>
        (CreatePlanService(harness), CreateTemplateService(harness));

    private static MaintenancePlanService CreatePlanService(BulkCreateAssetsHarness harness) =>
        new(harness.Db, harness.TenantProvider, harness.CreateRegistry());

    private static GlobalTemplateService CreateTemplateService(BulkCreateAssetsHarness harness) =>
        new(harness.Db);

    private static CreateFromTemplateRequest FromTemplate(
        BulkCreateAssetsHarness harness,
        Guid templateId,
        string? name = null,
        bool? autoGenerateEnabled = null)
    {
        var request = new CreateFromTemplateRequest
        {
            TemplateId = templateId,
            UnitId = harness.UnitId,
            AssetCategoryId = harness.CategoryId,
            Name = name,
        };

        return autoGenerateEnabled is null
            ? request
            : request with { AutoGenerateEnabled = autoGenerateEnabled.Value };
    }

    private static async Task<GlobalMaintenanceTemplate> EnsureAnvisaSeedAsync(BulkCreateAssetsHarness harness)
    {
        var existing = await harness.Db.GlobalMaintenanceTemplates
            .Include(template => template.Tasks)
            .FirstOrDefaultAsync(template => template.Id == GlobalTemplateSeed.AnvisaNr10TemplateId);

        if (existing is not null)
        {
            if (existing.Tasks.Count == 0)
            {
                AddAnvisaSeedTasks(existing);
                await harness.Db.SaveChangesAsync();
            }

            return existing;
        }

        var template = new GlobalMaintenanceTemplate
        {
            Name = GlobalTemplateSeed.AnvisaTemplateName,
            Description = GlobalTemplateSeed.AnvisaTemplateDescription,
            Frequency = GlobalTemplateSeed.AnvisaFrequency,
            Jurisdiction = GlobalTemplateSeed.AnvisaJurisdiction,
            TargetEquipmentType = GlobalTemplateSeed.AnvisaTargetEquipmentType,
            LibraryKey = GlobalTemplateSeed.AnvisaLibraryKey,
            Version = GlobalTemplateSeed.AnvisaVersion,
            Status = GlobalTemplateSeed.AnvisaStatus,
            SourceReferences = GlobalTemplateSeed.AnvisaSourceReferences,
        };
        SetEntityId(template, GlobalTemplateSeed.AnvisaNr10TemplateId);
        AddAnvisaSeedTasks(template);
        harness.Db.GlobalMaintenanceTemplates.Add(template);
        await harness.Db.SaveChangesAsync();
        return template;
    }

    private static void AddAnvisaSeedTasks(GlobalMaintenanceTemplate template)
    {
        AddSeedTask(
            template,
            GlobalTemplateSeed.TaskPhotoLocalId,
            "Foto do Local de Instalação (Visão Geral do Ambiente)",
            TaskInputType.Image,
            1,
            configuration: null);
        AddSeedTask(
            template,
            GlobalTemplateSeed.TaskPhotoFrontId,
            "Foto Frontal do Equipamento (Evaporadora)",
            TaskInputType.Image,
            2,
            configuration: null);
        AddSeedTask(
            template,
            GlobalTemplateSeed.TaskPhotoPanelId,
            "Foto do Quadro Elétrico / Painel de Comando (NR-10)",
            TaskInputType.Image,
            3,
            configuration: null);
        AddSeedTask(
            template,
            GlobalTemplateSeed.TaskDrainCleanId,
            "Limpeza da bandeja de drenagem e serpentina",
            TaskInputType.Checkbox,
            4,
            configuration: null);
        AddSeedTask(
            template,
            GlobalTemplateSeed.TaskCurrentMeasureId,
            "Medição da Corrente de Operação (Compressor)",
            TaskInputType.Number,
            5,
            """{"unit":"A"}""");
        AddSeedTask(
            template,
            GlobalTemplateSeed.TaskInsulationStateId,
            "Estado do isolamento térmico",
            TaskInputType.SingleChoice,
            6,
            """{"options":["Íntegro","Danificado","Inexistente"]}""");
    }

    private static void AddSeedTask(
        GlobalMaintenanceTemplate template,
        Guid id,
        string title,
        TaskInputType inputType,
        int order,
        string? configuration)
    {
        var task = new GlobalTemplateTask
        {
            GlobalMaintenanceTemplateId = template.Id,
            Title = title,
            InputType = inputType,
            Configuration = configuration,
            IsMandatory = true,
            Order = order,
        };
        SetEntityId(task, id);
        template.AddTask(task);
    }

    private static async Task<GlobalMaintenanceTemplate> InsertTemplateAsync(
        BulkCreateAssetsHarness harness,
        string name,
        string libraryKey,
        int version,
        GlobalTemplateStatus status,
        string jurisdiction,
        (string Title, TaskInputType InputType, bool IsMandatory, int Order, string? Configuration)[] tasks,
        string? sourceReferences = null)
    {
        var template = new GlobalMaintenanceTemplate
        {
            Name = name,
            Description = $"{name} description",
            Frequency = MaintenanceFrequency.Monthly,
            Jurisdiction = jurisdiction,
            TargetEquipmentType = "Ar Condicionado",
            LibraryKey = libraryKey,
            Version = version,
            Status = status,
            SourceReferences = sourceReferences,
        };

        foreach (var task in tasks)
        {
            template.AddTask(
                new GlobalTemplateTask
                {
                    GlobalMaintenanceTemplateId = template.Id,
                    Title = task.Title,
                    InputType = task.InputType,
                    IsMandatory = task.IsMandatory,
                    Order = task.Order,
                    Configuration = task.Configuration,
                });
        }

        harness.Db.GlobalMaintenanceTemplates.Add(template);
        await harness.Db.SaveChangesAsync();
        return template;
    }

    private static (string Title, TaskInputType InputType, bool IsMandatory, int Order, string? Configuration) TaskSpec(
        string title,
        TaskInputType inputType,
        int order,
        string? configuration = null,
        bool isMandatory = true) =>
        (title, inputType, isMandatory, order, configuration);

    private static void SetEntityId(Entity entity, Guid id) =>
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(entity, id);
}
