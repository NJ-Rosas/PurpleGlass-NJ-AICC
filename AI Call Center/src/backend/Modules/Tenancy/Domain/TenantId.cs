namespace PurpleGlass.Modules.Tenancy.Domain;

public readonly record struct TenantId(Guid Value)
{
    public static TenantId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}
