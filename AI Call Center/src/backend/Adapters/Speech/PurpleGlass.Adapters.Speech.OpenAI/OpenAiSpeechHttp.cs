using System.Net;
using System.Net.Http.Headers;
using PurpleGlass.Modules.Conversation.Application;

namespace PurpleGlass.Adapters.Speech.OpenAI;

internal static class OpenAiSpeechHttp
{
    public static void AddAuthentication(HttpRequestMessage request, string apiKey)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public static RuntimeFailure MapRecognitionFailure(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            new RuntimeFailure("speech_recognition_authentication_failed", "Speech recognition is not configured correctly.", false),
        HttpStatusCode.RequestTimeout =>
            new RuntimeFailure("speech_recognition_timeout", "Speech recognition timed out.", true),
        HttpStatusCode.TooManyRequests =>
            new RuntimeFailure("speech_recognition_rate_limited", "Speech recognition is temporarily busy.", true),
        >= HttpStatusCode.InternalServerError =>
            new RuntimeFailure("speech_recognition_unavailable", "Speech recognition is temporarily unavailable.", true),
        _ => new RuntimeFailure("speech_recognition_rejected", "Speech recognition rejected the audio request.", false),
    };

    public static RuntimeFailure MapSynthesisFailure(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            new RuntimeFailure("speech_synthesis_authentication_failed", "Speech synthesis is not configured correctly.", false),
        HttpStatusCode.RequestTimeout =>
            new RuntimeFailure("speech_synthesis_timeout", "Speech synthesis timed out.", true),
        HttpStatusCode.TooManyRequests =>
            new RuntimeFailure("speech_synthesis_rate_limited", "Speech synthesis is temporarily busy.", true),
        >= HttpStatusCode.InternalServerError =>
            new RuntimeFailure("speech_synthesis_unavailable", "Speech synthesis is temporarily unavailable.", true),
        _ => new RuntimeFailure("speech_synthesis_rejected", "Speech synthesis rejected the request.", false),
    };

    public static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
        {
            throw new OpenAiSpeechResponseTooLargeException();
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
                throw new OpenAiSpeechResponseTooLargeException();
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}

internal sealed class OpenAiSpeechResponseTooLargeException : Exception;
