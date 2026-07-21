namespace PurpleGlass.Modules.Conversation.Domain;

public readonly record struct ConversationId(Guid Value)
{
    public static ConversationId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct ConversationTurnId(Guid Value)
{
    public static ConversationTurnId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct CallSessionReference(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public readonly record struct TenantId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public readonly record struct LocationId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public readonly record struct CorrelationId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}
