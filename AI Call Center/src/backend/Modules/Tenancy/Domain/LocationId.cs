namespace PurpleGlass.Modules.Tenancy.Domain;

public readonly record struct LocationId(Guid Value)
{
    public static LocationId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}
