namespace Platform.Api.Modules.Rentals.Dtos;

public sealed record SetupStatusResponse(
    IReadOnlyList<SetupChecklistItemResponse> Checklist,
    IReadOnlyList<SetupResourceStateResponse> Resources);

public sealed record SetupChecklistItemResponse(string Key, string State);

public sealed record SetupResourceStateResponse(Guid Id, string State, IReadOnlyList<string> Missing);
