using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using PurpleGlass.Modules.CallManagement.Application;
using PurpleGlass.Modules.CallManagement.Contracts;
using PurpleGlass.Modules.Conversation.Application;
using PurpleGlass.Modules.Conversation.Contracts;
using PurpleGlass.Modules.Conversation.Domain;

namespace PurpleGlass.CallOrchestrator.Worker;

public sealed partial class CallOrchestrationService(
    CallManagementService calls,
    ConversationService conversations,
    ISpeechRecognizer speechRecognizer,
    IAiConversationRuntime aiRuntime,
    ISpeechSynthesizer speechSynthesizer,
    CallOrchestratorOptions options,
    TimeProvider timeProvider,
    ILogger<CallOrchestrationService> logger)
{
    public async Task<SimulatedCallResult> RunAsync(
        SimulatedCallRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        CallSummary? call = null;
        ConversationStatusProjection? conversation = null;
        var audioResponses = new List<SynthesizedAssistantResponse>();
        var stateTransitions = new List<string>();
        string? lastIntent = null;
        string outcome = "completed";

        using IDisposable? loggingScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["TenantId"] = request.TenantId,
            ["LocationId"] = request.LocationId,
            ["CorrelationId"] = request.CorrelationId,
            ["TraceId"] = request.TraceId,
        });
        LogOrchestrationStarted(logger, request.Direction, request.TenantId, request.LocationId, request.CorrelationId);

        try
        {
            call = await StartCallAsync(request, cancellationToken);
            stateTransitions.Add($"Call:{call.State}");
            if (request.Direction == SimulatedCallDirection.Outbound)
            {
                call = await calls.MarkRingingAsync(ChangeCall(request, call), cancellationToken);
                stateTransitions.Add($"Call:{call.State}");
            }
            call = await calls.MarkAnsweredAsync(ChangeCall(request, call), cancellationToken);
            stateTransitions.Add($"Call:{call.State}");

            conversation = await conversations.CreateAsync(new CreateConversation(
                request.TenantId, request.LocationId, call.CallId, request.CorrelationId,
                options.Conversation.Version, options.Conversation.Language, request.CausationId, request.TraceId), cancellationToken);
            conversation = await conversations.ActivateAsync(ChangeConversation(request, conversation), cancellationToken);
            stateTransitions.Add($"Conversation:{conversation.State}");
            call = await calls.MarkInConversationAsync(ChangeCall(request, call), cancellationToken);
            stateTransitions.Add($"Call:{call.State}");
            LogConversationStarted(logger, call.CallId, conversation.ConversationId, request.CorrelationId);

            conversation = await PersistAssistantAsync(
                request, conversation, DeterministicId(call.CallId, "greeting"), options.Conversation.Greeting,
                request.CausationId, false, false, cancellationToken);
            audioResponses.Add(await SynthesizeAsync(
                RuntimeContext(request, call.CallId, conversation.ConversationId, request.CausationId),
                options.Conversation.Greeting, cancellationToken));

            var processedInputIds = new HashSet<Guid>();
            int processedTurns = 0;
            bool escalated = false;
            bool shouldEnd = false;

            foreach (SimulatedCallerInput input in request.CallerInputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!processedInputIds.Add(input.InputId))
                {
                    LogDuplicateInput(logger, call.CallId, conversation.ConversationId, input.InputId);
                    continue;
                }

                IReadOnlyList<LiveTranscriptTurn> currentTranscript =
                    await conversations.GetTranscriptAsync(request.TenantId, conversation.ConversationId, cancellationToken);
                if (currentTranscript.Any(turn => turn.TurnId == input.InputId))
                {
                    LogDuplicateInput(logger, call.CallId, conversation.ConversationId, input.InputId);
                    continue;
                }

                if (processedTurns >= options.Conversation.MaximumTurns)
                {
                    outcome = "maximum_turns";
                    break;
                }

                RuntimeInvocationContext runtimeContext =
                    RuntimeContext(request, call.CallId, conversation.ConversationId, input.InputId);
                SpeechRecognitionResult recognition = await InvokeAdapterAsync(
                    speechRecognizer.AdapterKey, "recognize", options.RecognitionTimeout,
                    token => speechRecognizer.RecognizeAsync(
                        new SpeechRecognitionRequest(runtimeContext, options.Conversation.Language,
                            new SimulatedUtteranceInput(input.Text, input.Simulation)), token),
                    result => result.Failure, cancellationToken);

                _ = await conversations.AddCallerTurnAsync(new AddConversationTurn(
                    request.TenantId, conversation.ConversationId, conversation.Version, input.InputId,
                    recognition.RecognizedText, recognition.Confidence,
                    CausationId: input.InputId, TraceId: request.TraceId), cancellationToken);
                conversation = await conversations.GetAsync(
                    request.TenantId, conversation.ConversationId, cancellationToken);
                processedTurns++;

                currentTranscript = await conversations.GetTranscriptAsync(
                    request.TenantId, conversation.ConversationId, cancellationToken);
                AiResponseResult response = await InvokeAdapterAsync(
                    aiRuntime.AdapterKey, "generate", options.AiTimeout,
                    token => aiRuntime.GenerateAsync(new AiResponseRequest(
                        runtimeContext,
                        options.Conversation,
                        currentTranscript.Select(turn => new SanitizedConversationTurn(turn.Speaker, turn.Text)).ToArray(),
                        recognition.RecognizedText,
                        [],
                        new SafetyEscalationPolicy(
                            options.Conversation.SafetyPolicyVersion,
                            options.Conversation.EscalationKeywords,
                            options.Conversation.UrgentKeywords)), token),
                    result => result.Failure, cancellationToken);
                if (response.Intent is not (null or "conversation-end")) lastIntent = response.Intent;

                Guid assistantTurnId = DeterministicId(call.CallId, $"assistant:{input.InputId:N}");
                conversation = await PersistAssistantAsync(
                    request, conversation, assistantTurnId, response.AssistantText, input.InputId,
                    response.Intent == "urgent-safety", response.EscalationRequested, cancellationToken);
                audioResponses.Add(await SynthesizeAsync(
                    runtimeContext, response.AssistantText, cancellationToken));

                if (response.EscalationRequested)
                {
                    conversation = await conversations.RecordEscalationAsync(new RecordConversationEscalation(
                        request.TenantId, conversation.ConversationId, conversation.Version,
                        response.EscalationReason ?? "staff_follow_up", input.InputId, request.TraceId), cancellationToken);
                    escalated = true;
                    outcome = response.Intent == "urgent-safety" ? "urgent_escalation" : "escalated";
                    LogEscalation(logger, call.CallId, conversation.ConversationId, outcome);
                }

                shouldEnd = response.ShouldEndConversation || escalated;
                if (shouldEnd) break;
            }

            if (!shouldEnd && processedTurns >= options.Conversation.MaximumTurns) outcome = "maximum_turns";
            IReadOnlyList<LiveTranscriptTurn> transcript =
                await conversations.GetTranscriptAsync(request.TenantId, conversation.ConversationId, cancellationToken);
            var summary = new ConversationSummary(
                BuildSummary(transcript, outcome), lastIntent, outcome,
                outcome is "escalated" or "urgent_escalation", escalated,
                timeProvider.GetUtcNow(), options.Conversation.Version);
            CompletedConversationSummary completedSummary = await conversations.CompleteAsync(new CompleteConversation(
                request.TenantId, conversation.ConversationId, conversation.Version, summary,
                request.CausationId, request.TraceId), cancellationToken);
            conversation = await conversations.GetAsync(request.TenantId, conversation.ConversationId, cancellationToken);
            stateTransitions.Add($"Conversation:{conversation.State}");
            call = await calls.CompleteAsync(new CompleteCall(
                request.TenantId, call.CallId, call.Version, outcome, request.CausationId, request.TraceId), cancellationToken);
            stateTransitions.Add($"Call:{call.State}");
            transcript = await conversations.GetTranscriptAsync(request.TenantId, conversation.ConversationId, cancellationToken);

            LogOrchestrationCompleted(logger, call.CallId, conversation.ConversationId, outcome);
            return new(call.CallId, conversation.ConversationId, request.Direction, call.State, conversation.State,
                conversation.Escalated, outcome, stateTransitions, transcript, completedSummary, audioResponses);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CleanupAsync(request, call, conversation, "cancelled");
            LogOrchestrationCancelled(logger, call?.CallId ?? Guid.Empty, conversation?.ConversationId ?? Guid.Empty);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            string code = exception is CallOrchestrationException orchestrationException
                ? orchestrationException.Code : "orchestration_failed";
            await CleanupAsync(request, call, conversation, code);
            LogOrchestrationFailed(logger, call?.CallId ?? Guid.Empty, conversation?.ConversationId ?? Guid.Empty, code, exception);
            return await BuildFailureResultAsync(request, call, conversation, stateTransitions, audioResponses, code);
        }
    }

    private async Task<CallSummary> StartCallAsync(SimulatedCallRequest request, CancellationToken cancellationToken) =>
        request.Direction == SimulatedCallDirection.Inbound
            ? await calls.RegisterInboundAsync(new RegisterInboundCall(
                request.TenantId, request.LocationId, request.StartKey, request.FromNumber, request.ToNumber,
                request.CorrelationId, request.CausationId, request.TraceId), cancellationToken)
            : await calls.RequestOutboundAsync(new RequestOutboundCall(
                request.TenantId, request.LocationId, request.StartKey, request.FromNumber, request.ToNumber,
                request.CorrelationId, request.CausationId, request.TraceId), cancellationToken);

    private async Task<ConversationStatusProjection> PersistAssistantAsync(
        SimulatedCallRequest request,
        ConversationStatusProjection conversation,
        Guid turnId,
        string text,
        Guid? causationId,
        bool safetyFlagged,
        bool escalationFlagged,
        CancellationToken cancellationToken)
    {
        _ = await conversations.AddAssistantTurnAsync(new AddConversationTurn(
            request.TenantId, conversation.ConversationId, conversation.Version, turnId, text,
            SafetyFlagged: safetyFlagged, EscalationFlagged: escalationFlagged,
            CausationId: causationId, TraceId: request.TraceId), cancellationToken);
        return await conversations.GetAsync(request.TenantId, conversation.ConversationId, cancellationToken);
    }

    private async Task<SynthesizedAssistantResponse> SynthesizeAsync(
        RuntimeInvocationContext context,
        string text,
        CancellationToken cancellationToken)
    {
        SpeechSynthesisResult synthesis = await InvokeAdapterAsync(
            speechSynthesizer.AdapterKey, "synthesize", options.SynthesisTimeout,
            token => speechSynthesizer.SynthesizeAsync(new SpeechSynthesisRequest(
                context, text, options.Conversation.Language,
                new VoiceConfiguration(options.Conversation.VoiceId)), token),
            result => result.Failure, cancellationToken);
        return new(text, synthesis.AudioReference, synthesis.VoiceId);
    }

    private async Task<T> InvokeAdapterAsync<T>(
        string adapter,
        string operation,
        TimeSpan timeout,
        Func<CancellationToken, Task<T>> invoke,
        Func<T, RuntimeFailure?> getFailure,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= options.MaximumAdapterAttempts; attempt++)
        {
            long started = Stopwatch.GetTimestamp();
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                T result = await invoke(timeoutSource.Token);
                RuntimeFailure? failure = getFailure(result);
                LogAdapterInvocation(logger, adapter, operation, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    failure is null ? "success" : failure.Code);
                if (failure is null) return result;
                if (!failure.Retryable || attempt == options.MaximumAdapterAttempts)
                    throw new CallOrchestrationException(failure.Code, failure.SafeMessage);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                LogAdapterTimeout(logger, adapter, operation, attempt);
                if (attempt == options.MaximumAdapterAttempts)
                    throw new CallOrchestrationException($"{operation}_timeout", $"The {operation} adapter timed out.", exception);
            }

            LogAdapterRetry(logger, adapter, operation, attempt + 1);
        }

        throw new UnreachableException();
    }

    private async Task CleanupAsync(
        SimulatedCallRequest request,
        CallSummary? call,
        ConversationStatusProjection? conversation,
        string reason)
    {
        using var cleanupSource = new CancellationTokenSource(options.CleanupTimeout);
        try
        {
            if (conversation is not null)
            {
                ConversationStatusProjection current = await conversations.GetAsync(
                    request.TenantId, conversation.ConversationId, cleanupSource.Token);
                if (current.State is not ("Completed" or "Failed"))
                    _ = await conversations.FailAsync(new ChangeConversationState(
                        request.TenantId, current.ConversationId, current.Version,
                        request.CausationId, request.TraceId), cleanupSource.Token);
            }

            if (call is not null)
            {
                CallSummary current = await calls.GetAsync(request.TenantId, call.CallId, cleanupSource.Token);
                if (current.State is not ("Completed" or "Failed"))
                    _ = await calls.FailAsync(new FailCall(
                        request.TenantId, current.CallId, current.Version, reason,
                        request.CausationId, request.TraceId), cleanupSource.Token);
            }
        }
        catch (Exception cleanupException) when (cleanupException is not OperationCanceledException)
        {
            LogCleanupFailed(logger, call?.CallId ?? Guid.Empty, conversation?.ConversationId ?? Guid.Empty, cleanupException);
        }
    }

    private async Task<SimulatedCallResult> BuildFailureResultAsync(
        SimulatedCallRequest request,
        CallSummary? call,
        ConversationStatusProjection? conversation,
        IReadOnlyList<string> stateTransitions,
        IReadOnlyList<SynthesizedAssistantResponse> audioResponses,
        string code)
    {
        CallSummary? persistedCall = call is null ? null
            : await calls.GetAsync(request.TenantId, call.CallId, CancellationToken.None);
        ConversationStatusProjection? persistedConversation = conversation is null ? null
            : await conversations.GetAsync(request.TenantId, conversation.ConversationId, CancellationToken.None);
        IReadOnlyList<LiveTranscriptTurn> transcript = persistedConversation is null ? []
            : await conversations.GetTranscriptAsync(request.TenantId, persistedConversation.ConversationId, CancellationToken.None);
        return new(
            persistedCall?.CallId ?? Guid.Empty,
            persistedConversation?.ConversationId ?? Guid.Empty,
            request.Direction,
            persistedCall?.State ?? "Failed",
            persistedConversation?.State ?? "Failed",
            persistedConversation?.Escalated ?? false,
            "failed",
            stateTransitions.Concat([
                $"Conversation:{persistedConversation?.State ?? "Failed"}",
                $"Call:{persistedCall?.State ?? "Failed"}",
            ]).ToArray(),
            transcript,
            null,
            audioResponses,
            code);
    }

    private static RuntimeInvocationContext RuntimeContext(
        SimulatedCallRequest request,
        Guid callId,
        Guid conversationId,
        Guid? causationId) =>
        new(request.TenantId, request.LocationId, callId, conversationId,
            request.CorrelationId, causationId, request.TraceId);

    private static ChangeCallState ChangeCall(SimulatedCallRequest request, CallSummary call) =>
        new(request.TenantId, call.CallId, call.Version, request.CausationId, request.TraceId);

    private static ChangeConversationState ChangeConversation(
        SimulatedCallRequest request,
        ConversationStatusProjection conversation) =>
        new(request.TenantId, conversation.ConversationId, conversation.Version,
            request.CausationId, request.TraceId);

    private static string BuildSummary(IReadOnlyCollection<LiveTranscriptTurn> transcript, string outcome) =>
        $"Synthetic call completed with {transcript.Count} persisted turns. Outcome: {outcome}.";

    private static Guid DeterministicId(Guid callId, string discriminator)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{callId:N}|{discriminator}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static void Validate(SimulatedCallRequest request)
    {
        if (request.TenantId == Guid.Empty || request.LocationId == Guid.Empty)
            throw new ArgumentException("Tenant and location identifiers are required.", nameof(request));
        if (request.CorrelationId == Guid.Empty) throw new ArgumentException("CorrelationId is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.StartKey)) throw new ArgumentException("StartKey is required.", nameof(request));
        if (request.CallerInputs.Any(input => input.InputId == Guid.Empty || string.IsNullOrWhiteSpace(input.Text)))
            throw new ArgumentException("Every caller input requires an identifier and text.", nameof(request));
    }

    [LoggerMessage(1, LogLevel.Information, "Orchestration started for {Direction}; TenantId={TenantId}, LocationId={LocationId}, CorrelationId={CorrelationId}.")]
    private static partial void LogOrchestrationStarted(ILogger logger, SimulatedCallDirection direction, Guid tenantId, Guid locationId, Guid correlationId);
    [LoggerMessage(2, LogLevel.Information, "Conversation started; CallId={CallId}, ConversationId={ConversationId}, CorrelationId={CorrelationId}.")]
    private static partial void LogConversationStarted(ILogger logger, Guid callId, Guid conversationId, Guid correlationId);
    [LoggerMessage(3, LogLevel.Information, "Adapter invocation completed; AdapterType={Adapter}, Operation={Operation}, DurationMs={DurationMs}, Outcome={Outcome}.")]
    private static partial void LogAdapterInvocation(ILogger logger, string adapter, string operation, double durationMs, string outcome);
    [LoggerMessage(4, LogLevel.Warning, "Adapter timeout; AdapterType={Adapter}, Operation={Operation}, Attempt={Attempt}.")]
    private static partial void LogAdapterTimeout(ILogger logger, string adapter, string operation, int attempt);
    [LoggerMessage(5, LogLevel.Information, "Retrying adapter; AdapterType={Adapter}, Operation={Operation}, Attempt={Attempt}.")]
    private static partial void LogAdapterRetry(ILogger logger, string adapter, string operation, int attempt);
    [LoggerMessage(6, LogLevel.Information, "Escalation requested; CallId={CallId}, ConversationId={ConversationId}, Outcome={Outcome}.")]
    private static partial void LogEscalation(ILogger logger, Guid callId, Guid conversationId, string outcome);
    [LoggerMessage(7, LogLevel.Information, "Orchestration completed; CallId={CallId}, ConversationId={ConversationId}, Outcome={Outcome}.")]
    private static partial void LogOrchestrationCompleted(ILogger logger, Guid callId, Guid conversationId, string outcome);
    [LoggerMessage(8, LogLevel.Warning, "Orchestration cancelled; CallId={CallId}, ConversationId={ConversationId}.")]
    private static partial void LogOrchestrationCancelled(ILogger logger, Guid callId, Guid conversationId);
    [LoggerMessage(9, LogLevel.Error, "Orchestration failed; CallId={CallId}, ConversationId={ConversationId}, Outcome={Outcome}.")]
    private static partial void LogOrchestrationFailed(ILogger logger, Guid callId, Guid conversationId, string outcome, Exception exception);
    [LoggerMessage(10, LogLevel.Warning, "Cleanup failed; CallId={CallId}, ConversationId={ConversationId}.")]
    private static partial void LogCleanupFailed(ILogger logger, Guid callId, Guid conversationId, Exception exception);
    [LoggerMessage(11, LogLevel.Debug, "Duplicate caller input ignored; CallId={CallId}, ConversationId={ConversationId}, InputId={InputId}.")]
    private static partial void LogDuplicateInput(ILogger logger, Guid callId, Guid conversationId, Guid inputId);
}
