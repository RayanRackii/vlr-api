using System.Text.Json;
using Platform.Api.Modules.Pmoc.Controllers;
using Platform.Api.Modules.Pmoc.Dtos;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Pmoc;

public sealed class MaintenancePlanCoverageContractTests
{
    [Fact]
    public void Coverage_endpoint_has_no_server_side_status_filter()
    {
        var method = typeof(MaintenancePlansController).GetMethod(
            nameof(MaintenancePlansController.GetCoverage));
        Assert.NotNull(method);
        var parameters = method!.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal("id", parameters[0].Name);
        Assert.Equal(typeof(Guid), parameters[0].ParameterType);
        Assert.Equal(typeof(CancellationToken), parameters[1].ParameterType);
    }

    [Fact]
    public void Plan_list_contract_has_no_coverage_aggregates()
    {
        Assert.Null(typeof(MaintenancePlanResponse).GetProperty("EligibleAssetCount"));
        Assert.Null(typeof(MaintenancePlanResponse).GetProperty("AssetsNeedingAttention"));
        Assert.Null(typeof(MaintenancePlanResponse).GetProperty("Summary"));
        Assert.Null(typeof(MaintenancePlanResponse).GetProperty("WouldBeConsideredByGenerator"));
        Assert.Null(typeof(MaintenancePlanResponse).GetProperty("Coverage"));
    }

    [Fact]
    public void Asset_registry_contract_has_no_pmoc_coverage_fields()
    {
        Assert.Null(typeof(RegistryAssetListItem).GetProperty("DueStatus"));
        Assert.Null(typeof(RegistryAssetListItem).GetProperty("HistoryStatus"));
        Assert.Null(typeof(RegistryAssetListItem).GetProperty("NeedsAttention"));
        Assert.Null(typeof(RegistryAssetListItem).GetProperty("EffectiveNextDueDate"));
        Assert.Null(typeof(RegistryAssetListItem).GetProperty("OpenWorkOrder"));
    }

    [Fact]
    public void Final_coverage_dto_has_no_phase2_operational_or_calendar_fields()
    {
        Assert.Null(typeof(MaintenancePlanCoverageResponse).GetProperty("Frequency"));
        Assert.Null(typeof(MaintenancePlanCoverageResponse).GetProperty("LastDueDate"));
        Assert.Null(typeof(MaintenancePlanCoverageResponse).GetProperty("NextDueDate"));
        Assert.Null(typeof(MaintenancePlanCoverageResponse).GetProperty("IsDueToday"));
        Assert.Null(typeof(MaintenancePlanCoverageResponse).GetProperty("OperationalStatus"));

        Assert.Null(typeof(MaintenancePlanCoverageAssetItem).GetProperty("NextDueDate"));
        Assert.Null(typeof(MaintenancePlanCoverageAssetItem).GetProperty("OperationalStatus"));
        Assert.Null(typeof(MaintenancePlanCoverageAssetItem).GetProperty("IsDueToday"));
        Assert.Null(typeof(MaintenancePlanCoverageAssetItem).GetProperty("DueSoon"));
        Assert.NotNull(typeof(MaintenancePlanCoverageAssetItem).GetProperty("EffectiveNextDueDate"));
        Assert.NotNull(typeof(MaintenancePlanCoverageAssetItem).GetProperty("HistoryStatus"));
        Assert.NotNull(typeof(MaintenancePlanCoverageAssetItem).GetProperty("DueStatus"));

        Assert.Null(typeof(MaintenancePlanCoverageSummary).GetProperty("AssetsWithPmocHistory"));
        Assert.Null(typeof(MaintenancePlanCoverageSummary).GetProperty("CoveragePercent"));
        Assert.Null(typeof(MaintenancePlanCoverageSummary).GetProperty("CompliancePercent"));
        Assert.Null(typeof(MaintenancePlanCoverageSummary).GetProperty("AssetsDueSoon"));
    }

    [Fact]
    public void Json_uses_effective_next_due_and_omits_phase2_keys()
    {
        var json = JsonSerializer.Serialize(
            new MaintenancePlanCoverageResponse(
                Guid.NewGuid(),
                new DateOnly(2026, 9, 19),
                30,
                new DateOnly(2026, 10, 1),
                true,
                true,
                false,
                new MaintenancePlanCoverageSummary(0, 0, 0, 0, 0, 0, 0, 0),
                []),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.Contains("\"effectiveNextDueDate\"", JsonSerializer.Serialize(
            new MaintenancePlanCoverageAssetItem(
                Guid.NewGuid(),
                "Split",
                "AC-01",
                PmocHistoryStatus.NeverExecuted,
                null,
                new DateOnly(2026, 10, 1),
                PmocDueStatus.NotDue,
                false,
                null),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Assert.DoesNotContain("operationalStatus", json, StringComparison.Ordinal);
        Assert.DoesNotContain("frequency", json, StringComparison.Ordinal);
        Assert.DoesNotContain("lastDueDate", json, StringComparison.Ordinal);
        Assert.DoesNotContain("assetsWithPmocHistory", json, StringComparison.Ordinal);
        Assert.Contains("\"eligibleAssetCount\"", json, StringComparison.Ordinal);
        Assert.Contains("\"wouldBeConsideredByGenerator\"", json, StringComparison.Ordinal);
    }
}
