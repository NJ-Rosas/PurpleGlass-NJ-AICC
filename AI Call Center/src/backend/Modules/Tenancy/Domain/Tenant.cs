namespace PurpleGlass.Modules.Tenancy.Domain;

public sealed class Tenant
{
    private Tenant()
    {
    }

    public Tenant(TenantId id, string displayName)
    {
        Id = id;
        DisplayName = NormalizeRequired(displayName, nameof(displayName), 200);
        IsActive = true;
    }

    public TenantId Id { get; private set; }

    public string DisplayName { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    private static string NormalizeRequired(string value, string parameterName, int maximumLength)
    {
        string normalized = value.Trim();

        if (normalized.Length == 0 || normalized.Length > maximumLength)
        {
            throw new ArgumentException($"Value must contain between 1 and {maximumLength} characters.", parameterName);
        }

        return normalized;
    }
}
