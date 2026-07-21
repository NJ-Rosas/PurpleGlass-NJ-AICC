namespace PurpleGlass.Modules.CallManagement.Domain;

public readonly record struct CallSessionId(Guid Value)
{
    public static CallSessionId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}
