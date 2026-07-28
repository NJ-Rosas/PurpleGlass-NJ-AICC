using OpenAI.Responses;
using System.ClientModel;

namespace PurpleGlass.Adapters.AI.OpenAI;

public sealed record OpenAiConversationMessage(string Role, string Text);

public sealed record OpenAiResponsesRequest(
    string Model,
    string Instructions,
    IReadOnlyList<OpenAiConversationMessage> Messages,
    int MaximumOutputTokens);

public sealed record OpenAiResponsesResult(
    string OutputText,
    string? RequestId,
    string? Model,
    int InputTokens,
    int OutputTokens);

public interface IOpenAiResponsesGateway
{
    Task<OpenAiResponsesResult> CreateAsync(
        OpenAiResponsesRequest request,
        CancellationToken cancellationToken);
}

public sealed class OpenAiResponsesException(int statusCode, Exception innerException)
    : Exception("The OpenAI Responses request failed.", innerException)
{
    public int StatusCode { get; } = statusCode;
}

#pragma warning disable OPENAI001
public sealed class OpenAiResponsesGateway(ResponsesClient client) : IOpenAiResponsesGateway
{
    public async Task<OpenAiResponsesResult> CreateAsync(
        OpenAiResponsesRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var options = new CreateResponseOptions
        {
            Model = request.Model,
            Instructions = request.Instructions,
            MaxOutputTokenCount = request.MaximumOutputTokens,
            StoredOutputEnabled = false,
        };
        foreach (OpenAiConversationMessage message in request.Messages)
        {
            options.InputItems.Add(message.Role == "assistant"
                ? ResponseItem.CreateAssistantMessageItem(message.Text)
                : ResponseItem.CreateUserMessageItem(message.Text));
        }

        try
        {
            ResponseResult response = await client.CreateResponseAsync(options, cancellationToken);
            return new OpenAiResponsesResult(
                response.GetOutputText(),
                response.Id,
                response.Model,
                response.Usage?.InputTokenCount ?? 0,
                response.Usage?.OutputTokenCount ?? 0);
        }
        catch (ClientResultException exception)
        {
            throw new OpenAiResponsesException(exception.Status, exception);
        }
    }
}
#pragma warning restore OPENAI001
