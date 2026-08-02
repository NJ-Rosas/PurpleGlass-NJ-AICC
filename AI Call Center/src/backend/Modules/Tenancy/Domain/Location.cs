using PurpleGlass.SharedKernel;

namespace PurpleGlass.Modules.Tenancy.Domain;

public sealed class Location
{
    private Location()
    {
    }

    public Location(LocationId id, TenantId tenantId, string displayName, string timeZoneId,
        string defaultCallLanguageCode = SupportedCallLanguages.SystemFallbackCode)
    {
        Id = id;
        TenantId = tenantId;
        DisplayName = NormalizeDisplayName(displayName);
        TimeZoneId = ValidateTimeZone(timeZoneId);
        DefaultCallLanguageCode = SupportedCallLanguages.Require(defaultCallLanguageCode, nameof(defaultCallLanguageCode)).Code;
        IsActive = true;
        Version = 1;
    }

    public LocationId Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public string DisplayName { get; private set; } = string.Empty;

    public string TimeZoneId { get; private set; } = string.Empty;

    public string DefaultCallLanguageCode { get; private set; } = SupportedCallLanguages.SystemFallbackCode;

    public bool IsActive { get; private set; }

    public long Version { get; private set; }

    public bool Rename(string displayName)
    {
        if (!IsActive)
        {
            throw new InvalidOperationException("An inactive location cannot be renamed.");
        }

        string normalized = NormalizeDisplayName(displayName);

        if (string.Equals(DisplayName, normalized, StringComparison.Ordinal))
        {
            return false;
        }

        DisplayName = normalized;
        Version++;
        return true;
    }

    public bool ChangeDefaultCallLanguage(string languageCode)
    {
        if (!IsActive) throw new InvalidOperationException("An inactive location cannot be changed.");
        string normalized = SupportedCallLanguages.Require(languageCode, nameof(languageCode)).Code;
        if (DefaultCallLanguageCode.Equals(normalized, StringComparison.OrdinalIgnoreCase)) return false;
        DefaultCallLanguageCode = normalized;
        Version++;
        return true;
    }

    private static string NormalizeDisplayName(string displayName)
    {
        string normalized = displayName.Trim();

        if (normalized.Length is 0 or > 160)
        {
            throw new ArgumentException("Display name must contain between 1 and 160 characters.", nameof(displayName));
        }

        return normalized;
    }

    private static string ValidateTimeZone(string timeZoneId)
    {
        _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return timeZoneId;
    }
}
