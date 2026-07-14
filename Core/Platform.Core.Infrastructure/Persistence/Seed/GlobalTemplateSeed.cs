using Platform.Core.Domain.Enums;

namespace Platform.Core.Infrastructure.Persistence.Seed;

public static class GlobalTemplateSeed
{
    public static readonly Guid AnvisaNr10TemplateId =
        Guid.Parse("6f1c2a0e-4b9d-4f3a-9c7e-1d2a3b4c5d6e");

    public static readonly Guid TaskPhotoLocalId =
        Guid.Parse("7a2b3c4d-5e6f-4789-a012-3456789abc01");

    public static readonly Guid TaskPhotoFrontId =
        Guid.Parse("7a2b3c4d-5e6f-4789-a012-3456789abc02");

    public static readonly Guid TaskPhotoPanelId =
        Guid.Parse("7a2b3c4d-5e6f-4789-a012-3456789abc03");

    public static readonly Guid TaskDrainCleanId =
        Guid.Parse("7a2b3c4d-5e6f-4789-a012-3456789abc04");

    public static readonly Guid TaskCurrentMeasureId =
        Guid.Parse("7a2b3c4d-5e6f-4789-a012-3456789abc05");

    public static readonly Guid TaskInsulationStateId =
        Guid.Parse("7a2b3c4d-5e6f-4789-a012-3456789abc06");

    public static readonly DateTimeOffset CreatedAt =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public const string AnvisaTemplateName =
        "PMOC Padrão ANVISA (RE 09) + NR-10";

    public const string AnvisaTemplateDescription =
        "Modelo base alinhado à Lei 13.589/2018, Resolução Anvisa RE 09 e inspeções elétricas da NR-10 para equipamentos de climatização.";

    public const MaintenanceFrequency AnvisaFrequency = MaintenanceFrequency.Monthly;

    public const string AnvisaJurisdiction = "BR";

    public const string AnvisaTargetEquipmentType = "Ar Condicionado";
}
