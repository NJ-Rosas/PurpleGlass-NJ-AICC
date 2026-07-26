using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PurpleGlass.Modules.Conversation.Application;

namespace PurpleGlass.Adapters.AI.OpenAI;

public sealed class OpenAiConversationRuntime : IAiConversationRuntime
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient httpClient;
    private readonly OpenAiConversationOptions options;

    public OpenAiConversationRuntime(HttpClient httpClient, OpenAiConversationOptions options)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
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
        {
            return Failure(request, "ai_request_invalid", "The assistant request was invalid.", false);
        }

        List<OpenAiInputMessage> messages = BuildMessages(request, callerText);
        var payload = new OpenAiResponseRequest(
            options.Model,
            request.Configuration.SystemPrompt,
            messages,
            request.Configuration.MaximumOutputTokens,
            false);
        byte[] requestBody = JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);
        if (requestBody.Length > options.MaximumRequestBodyBytes)
        {
            return Failure(request, "ai_request_too_large", "The assistant request was too large.", false);
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, options.ResponsesEndpoint)
        {
            Content = JsonContent.Create(payload, options: SerializerOptions),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using HttpResponseMessage response = await httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Failure(request, MapHttpFailure(response.StatusCode));
            }

            byte[] responseBody = await ReadBoundedAsync(
                response.Content,
                options.MaximumResponseBodyBytes,
                cancellationToken);
            using JsonDocument document = JsonDocument.Parse(responseBody);
            string text = ExtractOutputText(document.RootElement).Trim();
            if (text.Length == 0)
            {
                return Failure(request, "ai_response_invalid", "The assistant returned an invalid response.", false);
            }

            if (text.Length > request.Configuration.MaximumResponseCharacters)
            {
                text = text[..request.Configuration.MaximumResponseCharacters].TrimEnd();
            }

            (int inputTokens, int outputTokens) = ReadUsage(document.RootElement);
            return new AiResponseResult(
                text,
                null,
                false,
                null,
                false,
                new AiUsageMetadata(inputTokens, outputTokens, "tokens"),
                request.Configuration.Version);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure(request, "ai_timeout", "The assistant timed out.", true);
        }
        catch (HttpRequestException)
        {
            return Failure(request, "ai_network_failed", "The assistant provider is temporarily unavailable.", true);
        }
        catch (IOException)
        {
            return Failure(request, "ai_network_failed", "The assistant provider is temporarily unavailable.", true);
        }
        catch (JsonException)
        {
            return Failure(request, "ai_response_invalid", "The assistant returned an invalid response.", false);
        }
        catch (OpenAiResponseTooLargeException)
        {
            return Failure(request, "ai_response_too_large", "The assistant response exceeded the safe size limit.", false);
        }
    }

    private static List<OpenAiInputMessage> BuildMessages(AiResponseRequest request, string callerText)
    {
        SanitizedConversationTurn[] history = request.ExistingTurns
            .TakeLast(request.Configuration.MaximumHistoryTurns)
            .Where(turn => !string.IsNullOrWhiteSpace(turn.Text))
            .ToArray();
        if (history.Length > 0
            && history[^1].Speaker.Equals("Caller", StringComparison.OrdinalIgnoreCase)
            && history[^1].Text.Trim().Equals(callerText, StringComparison.Ordinal))
        {
            history = history[..^1];
        }

        var messages = new List<OpenAiInputMessage>(history.Length + 1);
        foreach (SanitizedConversationTurn turn in history)
        {
            string? role = turn.Speaker.Equals("Caller", StringComparison.OrdinalIgnoreCase)
                ? "user"
                : turn.Speaker.Equals("Assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : null;
            if (role is not null)
            {
                messages.Add(new OpenAiInputMessage(role, turn.Text.Trim()));
            }
        }

        messages.Add(new OpenAiInputMessage("user", callerText));
        return messages;
    }

    private static RuntimeFailure MapHttpFailure(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            new RuntimeFailure("ai_authentication_failed", "The assistant provider is not configured correctly.", false),
        HttpStatusCode.RequestTimeout =>
            new RuntimeFailure("ai_timeout", "The assistant timed out.", true),
        HttpStatusCode.TooManyRequests =>
            new RuntimeFailure("ai_rate_limited", "The assistant provider is temporarily busy.", true),
        >= HttpStatusCode.InternalServerError =>
            new RuntimeFailure("ai_provider_unavailable", "The assistant provider is temporarily unavailable.", true),
        _ => new RuntimeFailure("ai_request_rejected", "The assistant provider rejected the request.", false),
    };

    private static AiResponseResult Failure(AiResponseRequest request, RuntimeFailure failure) => new(
        string.Empty,
        null,
        false,
        null,
        false,
        new AiUsageMetadata(0, 0, "tokens"),
        request.Configuration.Version,
        failure);

    private static AiResponseResult Failure(
        AiResponseRequest request,
        string code,
        string safeMessage,
        bool retryable) => Failure(request, new RuntimeFailure(code, safeMessage, retryable));

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
        {
            throw new OpenAiResponseTooLargeException();
        }

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
        byte[] buffer = new byte[8 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maximumBytes)
            {
                throw new OpenAiResponseTooLargeException();
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static string ExtractOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out JsonElement direct)
            && direct.ValueKind == JsonValueKind.String)
        {
            return direct.GetString() ?? string.Empty;
        }

        if (!root.TryGetProperty("output", out JsonElement output)
            || output.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        foreach (JsonElement item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out JsonElement content)
                || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement part in content.EnumerateArray())
            {
                if (part.TryGetProperty("type", out JsonElement type)
                    && type.ValueKind == JsonValueKind.String
                    && type.GetString() == "output_text"
                    && part.TryGetProperty("text", out JsonElement value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    if (text.Length > 0)
                    {
                        text.AppendLine();
                    }

                    text.Append(value.GetString());
                }
            }
        }

        return text.ToString();
    }

    private static (int InputTokens, int OutputTokens) ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out JsonElement usage)
            || usage.ValueKind != JsonValueKind.Object)
        {
            return (0, 0);
        }

        return (
            ReadNonNegativeInt32(usage, "input_tokens"),
            ReadNonNegativeInt32(usage, "output_tokens"));
    }

    private static int ReadNonNegativeInt32(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out long parsed))
        {
            return 0;
        }

        return (int)Math.Clamp(parsed, 0, int.MaxValue);
    }

    private sealed record OpenAiResponseRequest(
        string Model,
        string Instructions,
        IReadOnlyList<OpenAiInputMessage> Input,
        int MaxOutputTokens,
        bool Store);

    private sealed record OpenAiInputMessage(string Role, string Content);

    private sealed class OpenAiResponseTooLargeException : Exception;
}
