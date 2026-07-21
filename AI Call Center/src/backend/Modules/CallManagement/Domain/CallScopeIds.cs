namespace PurpleGlass.Modules.CallManagement.Domain;

public readonly record struct TenantId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public readonly record struct LocationId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}
