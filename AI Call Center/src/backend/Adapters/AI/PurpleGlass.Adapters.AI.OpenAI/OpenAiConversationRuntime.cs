using PurpleGlass.Modules.Conversation.Application;

namespace PurpleGlass.Adapters.AI.OpenAI;

public sealed class OpenAiConversationRuntime : IAiConversationRuntime
{
    private const string BaselineInstructions =
        "You are speaking with a caller by phone. Return plain conversational text without Markdown. " +
        "Keep responses reasonably concise and usually ask one useful question at a time. " +
        "Do not invent office information or patient data. Do not claim an appointment was created " +
        "or that any external action was performed. Do not diagnose dental conditions or pretend tools exist.";
    private readonly IOpenAiResponsesGateway gateway;
    private readonly OpenAiConversationOptions options;

    public OpenAiConversationRuntime(
        IOpenAiResponsesGateway gateway,
        OpenAiConversationOptions options)
    {
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        this.options = (options ?? throw new ArgumentNullException(nameof(options))).Validate();
    }

    public string AdapterKey => "openai";

    public async Task<AiResponseResult> GenerateAsync(
        AiResponseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        string callerText = request.CurrentCallerTurn.Trim();
        if (callerText.Length == 0)
            return Failure(request, "ai_request_invalid", "The assistant request was invalid.", false);

        IReadOnlyList<OpenAiConversationMessage> messages = BuildMessages(request, callerText);
        var providerRequest = new OpenAiResponsesRequest(
            options.Model,
            BuildInstructions(request.Configuration),
            messages,
            request.Configuration.MaximumOutputTokens);

        try
        {
            OpenAiResponsesResult providerResult = await gateway.CreateAsync(providerRequest, cancellationToken);
            string text = providerResult.OutputText.Trim();
            if (text.Length == 0)
                return Failure(request, "ai_response_invalid", "The assistant returned an invalid response.", false);

            text = VoiceResponsePolicy.Constrain(text, request.Configuration.MaximumResponseCharacters);
            return new AiResponseResult(
                text,
                null,
                false,
                null,
                false,
                new AiUsageMetadata(providerResult.InputTokens, providerResult.OutputTokens, "tokens"),
                request.Configuration.Version,
                Provider: AdapterKey,
                Model: providerResult.Model ?? options.Model,
                ProviderRequestId: providerResult.RequestId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OpenAiResponsesException exception)
        {
            return Failure(request, MapProviderFailure(exception.StatusCode));
        }
        catch (HttpRequestException)
        {
            return Failure(request, "ai_network_failed", "The assistant provider is temporarily unavailable.", true);
        }
        catch (IOException)
        {
            return Failure(request, "ai_network_failed", "The assistant provider is temporarily unavailable.", true);
        }
    }

    internal static IReadOnlyList<OpenAiConversationMessage> BuildMessages(
        AiResponseRequest request,
        string callerText)
    {
        SanitizedConversationTurn[] history = request.ExistingTurns
            .Where(turn => !string.IsNullOrWhiteSpace(turn.Text)
                && (turn.Speaker.Equals("Caller", StringComparison.OrdinalIgnoreCase)
                    || turn.Speaker.Equals("Assistant", StringComparison.OrdinalIgnoreCase)))
            .TakeLast(request.Configuration.MaximumHistoryTurns)
            .ToArray();
        if (history.Length > 0
            && history[^1].Speaker.Equals("Caller", StringComparison.OrdinalIgnoreCase)
            && history[^1].Text.Trim().Equals(callerText, StringComparison.Ordinal))
        {
            history = history[..^1];
        }

        var messages = new List<OpenAiConversationMessage>(history.Length + 1);
        foreach (SanitizedConversationTurn turn in history)
        {
            string role = turn.Speaker.Equals("Caller", StringComparison.OrdinalIgnoreCase)
                ? "user" : "assistant";
            messages.Add(new OpenAiConversationMessage(role, turn.Text.Trim()));
        }

        messages.Add(new OpenAiConversationMessage("user", callerText));
        return messages.TakeLast(request.Configuration.MaximumHistoryTurns).ToArray();
    }

    internal static string BuildInstructions(ConversationRuntimeConfiguration configuration)
    {
        var parts = new List<string> { BaselineInstructions, configuration.SystemPrompt.Trim() };
        if (!IsUnavailable(configuration.OfficeName))
            parts.Add($"You represent the configured office or location named {configuration.OfficeName.Trim()}.");
        if (!IsUnavailable(configuration.OfficeLocation))
            parts.Add($"The trusted configured location is {configuration.OfficeLocation.Trim()}.");
        parts.Add($"Use the configured call language {configuration.Language.Trim()}.");
        return string.Join(' ', parts);
    }

    private static bool IsUnavailable(string value) =>
        string.IsNullOrWhiteSpace(value)
        || value.Trim().Equals("not configured", StringComparison.OrdinalIgnoreCase);

    private static RuntimeFailure MapProviderFailure(int statusCode) => statusCode switch
    {
        401 or 403 => new RuntimeFailure(
            "ai_authentication_failed", "The assistant provider is not configured correctly.", false),
        408 => new RuntimeFailure("ai_timeout", "The assistant timed out.", true),
        429 => new RuntimeFailure("ai_rate_limited", "The assistant provider is temporarily busy.", true),
        >= 500 => new RuntimeFailure(
            "ai_provider_unavailable", "The assistant provider is temporarily unavailable.", true),
        0 => new RuntimeFailure("ai_network_failed", "The assistant provider is temporarily unavailable.", true),
        _ => new RuntimeFailure("ai_request_rejected", "The assistant provider rejected the request.", false),
    };

    private AiResponseResult Failure(AiResponseRequest request, RuntimeFailure failure) => new(
        string.Empty,
        null,
        false,
        null,
        false,
        new AiUsageMetadata(0, 0, "tokens"),
        request.Configuration.Version,
        failure,
        AdapterKey,
        options.Model);

    private AiResponseResult Failure(
        AiResponseRequest request,
        string code,
        string safeMessage,
        bool retryable) => Failure(request, new RuntimeFailure(code, safeMessage, retryable));
}
