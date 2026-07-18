namespace PurpleGlass.Modules.Tenancy.Contracts;

public sealed record TenantSummary(
    Guid TenantId,
    string TenantDisplayName,
    Guid LocationId,
    string LocationDisplayName,
    string TimeZoneId,
    long Version);

public sealed record UpdateLocationDisplayNameRequest(string DisplayName, long ExpectedVersion);

public sealed record LocationDisplayNameChanged(
    Guid LocationId,
    string DisplayName,
    long Version,
    DateTimeOffset ChangedAtUtc);
