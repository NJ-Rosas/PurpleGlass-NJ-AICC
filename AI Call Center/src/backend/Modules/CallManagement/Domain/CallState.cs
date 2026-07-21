namespace PurpleGlass.Modules.CallManagement.Domain;

public enum CallDirection
{
    Inbound = 1,
    Outbound = 2,
}

public enum CallState
{
    Received = 1,
    Requested = 2,
    Ringing = 3,
    Answered = 4,
    InConversation = 5,
    Completed = 6,
    Failed = 7,
}
