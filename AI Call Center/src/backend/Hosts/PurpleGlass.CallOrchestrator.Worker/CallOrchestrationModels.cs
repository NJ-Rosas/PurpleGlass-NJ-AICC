using PurpleGlass.Modules.CallManagement.Contracts;
using PurpleGlass.Modules.Conversation.Contracts;

namespace PurpleGlass.CallOrchestrator.Worker;

public enum SimulatedCallDirection
{
    Inbound,
    Outbound,
}

public sealed record SimulatedCallerInput(Guid InputId, string Text, string? Simulation = null);

public sealed record SimulatedCallRequest(
    Guid TenantId,
    Guid LocationId,
    SimulatedCallDirection Direction,
    string StartKey,
    string FromNumber,
    string ToNumber,
    IReadOnlyList<SimulatedCallerInput> CallerInputs,
    Guid CorrelationId,
    Guid? CausationId = null,
    string? TraceId = null);

public sealed record SynthesizedAssistantResponse(string Text, string AudioReference, string VoiceId);

public sealed record SimulatedCallResult(
    Guid CallId,
    Guid ConversationId,
    SimulatedCallDirection Direction,
    string CallState,
    string ConversationState,
    bool Escalated,
    string Outcome,
    IReadOnlyList<string> StateTransitions,
    IReadOnlyList<LiveTranscriptTurn> Transcript,
    CompletedConversationSummary? Summary,
    IReadOnlyList<SynthesizedAssistantResponse> AssistantResponses,
    string? FailureCode = null);

public sealed class CallOrchestrationException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
