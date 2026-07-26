namespace PurpleGlass.WebBff;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";
    public bool AllowDevelopmentAuthentication { get; init; }
    public bool AllowSyntheticDataOnly { get; init; } = true;
    public bool RequireHttps { get; init; } = true;
    public bool ForceSecureCookies { get; init; }
    public string CookieName { get; init; } = "__Host-PurpleGlass.Session";
    public int SessionMinutes { get; init; } = 30;
    public string ProductionOrigin { get; init; } = string.Empty;
    public string[] DevelopmentOrigins { get; init; } = [];
    public string DataProtectionKeysPath { get; init; } = string.Empty;
    public OidcOptions Oidc { get; init; } = new();
    public IdentityMappingOptions[] IdentityMappings { get; init; } = [];
}

public sealed class IdentityMappingOptions
{
    public Guid UserId { get; init; }
    public Guid MembershipId { get; init; }
    public string ExternalSubject { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public Guid TenantId { get; init; }
    public Guid[] LocationIds { get; init; } = [];
    public Guid ActiveLocationId { get; init; }
    public string Role { get; init; } = string.Empty;
    public bool Active { get; init; } = true;
}

public sealed class OidcOptions
{
    public string Authority { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
    public string CallbackPath { get; init; } = "/signin-oidc";
}

public sealed class SafetyOptions
{
    public bool EnableRealTelephony { get; init; }
    public bool EnableRealAI { get; init; }
    public bool EnableRealSpeech { get; init; }
    public bool EnableOpenDental { get; init; }
    public bool AllowSensitiveData { get; init; }
}

public static class ProductionSecurityValidator
{
    public static void Validate(
        SecurityOptions security,
        SafetyOptions safety,
        IHostEnvironment environment,
        string allowedHosts)
    {
        if (!environment.IsDevelopment() && security.AllowDevelopmentAuthentication)
            throw Invalid("development authentication must be disabled");
        if (!security.AllowSyntheticDataOnly || safety.AllowSensitiveData)
            throw Invalid("synthetic-data-only mode must remain enabled");
        if (safety.EnableOpenDental)
            throw Invalid("clinical external providers must remain disabled");
        if (!environment.IsDevelopment() && (safety.EnableRealAI || safety.EnableRealSpeech))
            throw Invalid("real AI and speech external providers are permitted only in explicit development mode");

        if (!environment.IsProduction()) return;

        if (!security.RequireHttps) throw Invalid("HTTPS is required");
        if (string.IsNullOrWhiteSpace(security.Oidc.Authority)
            || string.IsNullOrWhiteSpace(security.Oidc.ClientId)
            || string.IsNullOrWhiteSpace(security.Oidc.ClientSecret)
            || string.IsNullOrWhiteSpace(security.Oidc.CallbackPath))
            throw Invalid("OpenID Connect configuration is incomplete");
        if (!Uri.TryCreate(security.ProductionOrigin, UriKind.Absolute, out Uri? origin)
            || origin.Scheme != Uri.UriSchemeHttps)
            throw Invalid("a valid HTTPS production origin is required");
        if (string.IsNullOrWhiteSpace(security.DataProtectionKeysPath))
            throw Invalid("persistent data-protection keys are required");
        if (security.IdentityMappings.Length == 0)
            throw Invalid("at least one server-owned identity membership mapping is required");
        if (security.IdentityMappings.Any(mapping =>
            mapping.UserId == Guid.Empty || mapping.MembershipId == Guid.Empty || mapping.TenantId == Guid.Empty
            || mapping.LocationIds.Length == 0 || !mapping.LocationIds.Contains(mapping.ActiveLocationId)
            || string.IsNullOrWhiteSpace(mapping.ExternalSubject) || string.IsNullOrWhiteSpace(mapping.DisplayName)
            || !Enum.TryParse<PurpleGlass.Modules.Identity.Domain.MembershipRole>(mapping.Role, true, out _)))
            throw Invalid("identity membership mappings are invalid");
        if (string.IsNullOrWhiteSpace(allowedHosts) || allowedHosts.Contains('*', StringComparison.Ordinal))
            throw Invalid("AllowedHosts must be restricted");
        if (security.DevelopmentOrigins.Length != 0)
            throw Invalid("development CORS origins are not allowed");
    }

    private static InvalidOperationException Invalid(string category) =>
        new($"production_security_configuration_invalid: {category}.");
}
