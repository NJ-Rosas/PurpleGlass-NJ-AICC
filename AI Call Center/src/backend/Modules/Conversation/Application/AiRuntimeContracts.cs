namespace PurpleGlass.Modules.Conversation.Application;

public sealed record SanitizedConversationTurn(string Speaker, string Text);

public sealed record AiToolDefinition(string Name, string Description);

public sealed record ConversationAgentBehavior(
    string Instructions,
    IReadOnlySet<string> ConversationalCapabilities,
    IReadOnlySet<string> UnsupportedActions);

public sealed record AgentLanguageContext(
    string ActiveLanguageCode,
    IReadOnlyList<string> SupportedLanguageCodes,
    bool SwitchAccepted,
    string PriorLanguageCode,
    string CurrentLanguageCode,
    string SwitchReason,
    bool AcknowledgementNeeded,
    bool UnsupportedFallbackSelected);

public sealed record AiResponseRequest(
    RuntimeInvocationContext Context,
    ConversationRuntimeConfiguration Configuration,
    ConversationAgentBehavior Behavior,
    IReadOnlyList<SanitizedConversationTurn> ExistingTurns,
    string CurrentCallerTurn,
    IReadOnlyList<AiToolDefinition> AvailableTools,
    SafetyEscalationPolicy SafetyPolicy,
    AgentLanguageContext? LanguageContext = null);

public sealed record AiUsageMetadata(int InputUnits, int OutputUnits, string Meter = "synthetic-units");

public sealed record AiResponseResult(
    string AssistantText,
    string? Intent,
    bool EscalationRequested,
    string? EscalationReason,
    bool ShouldEndConversation,
    AiUsageMetadata Usage,
    string ConfigurationVersion,
    RuntimeFailure? Failure = null,
    string? Provider = null,
    string? Model = null,
    string? ProviderRequestId = null);

public interface IAiConversationRuntime
{
    string AdapterKey { get; }

    Task<AiResponseResult> GenerateAsync(
        AiResponseRequest request,
        CancellationToken cancellationToken);
}
