namespace PurpleGlass.Modules.Conversation.Application;

public sealed record SanitizedConversationTurn(string Speaker, string Text);

public sealed record AiToolDefinition(string Name, string Description);

public sealed record AiResponseRequest(
    RuntimeInvocationContext Context,
    ConversationRuntimeConfiguration Configuration,
    IReadOnlyList<SanitizedConversationTurn> ExistingTurns,
    string CurrentCallerTurn,
    IReadOnlyList<AiToolDefinition> AvailableTools,
    SafetyEscalationPolicy SafetyPolicy);

public sealed record AiUsageMetadata(int InputUnits, int OutputUnits, string Meter = "synthetic-units");

public sealed record AiResponseResult(
    string AssistantText,
    string? Intent,
    bool EscalationRequested,
    string? EscalationReason,
    bool ShouldEndConversation,
    AiUsageMetadata Usage,
    string ConfigurationVersion,
    RuntimeFailure? Failure = null);

public interface IAiConversationRuntime
{
    string AdapterKey { get; }

    Task<AiResponseResult> GenerateAsync(
        AiResponseRequest request,
        CancellationToken cancellationToken);
}
