namespace PurpleGlass.Modules.Tenancy.Contracts;

public sealed record TenantSummary(
    Guid TenantId,
    string TenantDisplayName,
    Guid LocationId,
    string LocationDisplayName,
    string TimeZoneId,
    string DefaultCallLanguageCode,
    IReadOnlyList<SupportedCallLanguageSummary> SupportedCallLanguages,
    long Version);

public sealed record UpdateLocationDisplayNameRequest(string DisplayName, long ExpectedVersion);

public sealed record UpdateLocationDefaultCallLanguageRequest(string LanguageCode, long ExpectedVersion);

public sealed record SupportedCallLanguageSummary(string Code, string DisplayName);

public sealed record LocationDisplayNameChanged(
    Guid LocationId,
    string DisplayName,
    long Version,
    DateTimeOffset ChangedAtUtc);

public sealed record LocationDefaultCallLanguageChanged(
    Guid LocationId,
    string PreviousLanguageCode,
    string LanguageCode,
    long Version,
    DateTimeOffset ChangedAtUtc);
