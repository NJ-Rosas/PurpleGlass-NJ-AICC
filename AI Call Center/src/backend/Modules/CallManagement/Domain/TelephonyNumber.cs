namespace PurpleGlass.Modules.CallManagement.Domain;

public sealed class TelephonyNumber
{
    private TelephonyNumber() { }

    public TelephonyNumber(
        Guid id,
        TenantId tenantId,
        LocationId? locationId,
        string provider,
        string normalizedNumber,
        string? providerNumberId,
        bool inboundEnabled,
        bool outboundEnabled)
    {
        if (id == Guid.Empty) throw new ArgumentException("Identifier is required.", nameof(id));
        if (tenantId.Value == Guid.Empty) throw new ArgumentException("Tenant identifier is required.", nameof(tenantId));
        Id = id;
        TenantId = tenantId;
        LocationId = locationId;
        Provider = Required(provider, nameof(provider), 50);
        NormalizedNumber = PhoneNumber.Normalize(normalizedNumber);
        ProviderNumberId = Optional(providerNumberId, nameof(providerNumberId), 200);
        InboundEnabled = inboundEnabled;
        OutboundEnabled = outboundEnabled;
        IsActive = true;
        Version = 1;
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public LocationId? LocationId { get; private set; }
    public string Provider { get; private set; } = string.Empty;
    public string NormalizedNumber { get; private set; } = string.Empty;
    public string? ProviderNumberId { get; private set; }
    public bool InboundEnabled { get; private set; }
    public bool OutboundEnabled { get; private set; }
    public bool IsActive { get; private set; }
    public long Version { get; private set; }

    public void Update(LocationId? locationId, string? providerNumberId, bool inboundEnabled, bool outboundEnabled, bool active)
    {
        LocationId = locationId;
        ProviderNumberId = Optional(providerNumberId, nameof(providerNumberId), 200);
        InboundEnabled = inboundEnabled;
        OutboundEnabled = outboundEnabled;
        IsActive = active;
        Version++;
    }

    private static string Required(string value, string name, int maximumLength)
    {
        string normalized = value.Trim();
        return normalized.Length is 0 || normalized.Length > maximumLength
            ? throw new ArgumentException($"Value must contain between 1 and {maximumLength} characters.", name)
            : normalized;
    }

    private static string? Optional(string? value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string normalized = value.Trim();
        return normalized.Length > maximumLength
            ? throw new ArgumentException($"Value cannot exceed {maximumLength} characters.", name)
            : normalized;
    }
}
