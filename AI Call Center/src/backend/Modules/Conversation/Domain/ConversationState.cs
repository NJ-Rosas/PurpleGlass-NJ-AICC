namespace PurpleGlass.Modules.Conversation.Domain;

public enum ConversationState
{
    Created = 1,
    Active = 2,
    Completed = 3,
    Failed = 4,
}

public enum SpeakerRole
{
    Caller = 1,
    Assistant = 2,
    Staff = 3,
    System = 4,
}
