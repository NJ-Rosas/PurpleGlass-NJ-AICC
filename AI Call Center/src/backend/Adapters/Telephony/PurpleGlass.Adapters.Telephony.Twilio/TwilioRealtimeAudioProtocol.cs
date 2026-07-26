using System.Buffers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace PurpleGlass.Adapters.Telephony.Twilio;

public sealed class TwilioRealtimeAudioException : Exception
{
    public TwilioRealtimeAudioException(string code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;

    public string Code { get; }
}

internal sealed class TwilioJsonMessage : IDisposable
{
    private byte[]? buffer;
    private readonly int length;

    public TwilioJsonMessage(byte[] buffer, int length, JsonDocument document)
    {
        this.buffer = buffer;
        this.length = length;
        Document = document;
    }

    public JsonDocument Document { get; }

    public void Dispose()
    {
        Document.Dispose();
        byte[]? rented = Interlocked.Exchange(ref buffer, null);
        if (rented is null) return;
        CryptographicOperations.ZeroMemory(rented.AsSpan(0, length));
        ArrayPool<byte>.Shared.Return(rented);
    }
}

internal static class TwilioRealtimeAudioProtocol
{
    public const string Provider = "Twilio";
    public const string SourceEncoding = "audio/x-mulaw";
    public const int TelephonySampleRate = 8_000;
    public const int TelephonyChannels = 1;
    public const int SidLength = 34;

    public static async ValueTask<TwilioJsonMessage?> ReceiveMessageAsync(
        WebSocket socket,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(maximumBytes);
        int length = 0;
        bool ownershipTransferred = false;
        try
        {
            while (true)
            {
                if (length == maximumBytes)
                    throw ProtocolError("provider_media_message_too_large", "Provider media message exceeded its size limit.");

                ValueWebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(length, maximumBytes - length), cancellationToken);
                }
                catch (WebSocketException exception)
                {
                    throw new TwilioRealtimeAudioException(
                        "provider_media_receive_failed", "Provider media connection failed.", exception);
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (length != 0)
                        throw ProtocolError("provider_media_message_invalid", "Provider media message was incomplete.");
                    return null;
                }
                if (result.MessageType != WebSocketMessageType.Text)
                    throw ProtocolError("provider_media_message_type_invalid", "Provider media messages must be text.");

                length += result.Count;
                if (!result.EndOfMessage) continue;
                if (length == 0)
                    throw ProtocolError("provider_media_message_invalid", "Provider media message was empty.");

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(
                        buffer.AsMemory(0, length),
                        new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 });
                }
                catch (JsonException exception)
                {
                    throw new TwilioRealtimeAudioException(
                        "provider_media_json_invalid", "Provider media message was invalid.", exception);
                }

                ownershipTransferred = true;
                return new TwilioJsonMessage(buffer, length, document);
            }
        }
        finally
        {
            if (!ownershipTransferred)
            {
                CryptographicOperations.ZeroMemory(buffer.AsSpan(0, length));
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    public static JsonElement RequireObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
            throw ProtocolError("provider_media_metadata_missing", "Provider media metadata was missing.");
        return value;
    }

    public static string RequireString(JsonElement parent, string name, int maximumLength = 200)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw ProtocolError("provider_media_metadata_missing", "Provider media metadata was missing.");
        string? result = value.GetString();
        if (string.IsNullOrWhiteSpace(result) || result.Length > maximumLength)
            throw ProtocolError("provider_media_metadata_invalid", "Provider media metadata was invalid.");
        return result;
    }

    public static int RequireInt32(JsonElement parent, string name, int minimum, int maximum)
    {
        if (!parent.TryGetProperty(name, out JsonElement value))
            throw ProtocolError("provider_media_metadata_missing", "Provider media metadata was missing.");
        int parsed;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out parsed))
        {
        }
        else if (value.ValueKind == JsonValueKind.String
                 && int.TryParse(value.GetString(), System.Globalization.NumberStyles.None,
                     System.Globalization.CultureInfo.InvariantCulture, out parsed))
        {
        }
        else
        {
            throw ProtocolError("provider_media_metadata_invalid", "Provider media metadata was invalid.");
        }

        if (parsed < minimum || parsed > maximum)
            throw ProtocolError("provider_media_metadata_invalid", "Provider media metadata was invalid.");
        return parsed;
    }

    public static long RequirePositiveInt64(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value))
            throw ProtocolError("provider_media_metadata_missing", "Provider media metadata was missing.");
        long parsed;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out parsed))
        {
        }
        else if (value.ValueKind == JsonValueKind.String
                 && long.TryParse(value.GetString(), System.Globalization.NumberStyles.None,
                     System.Globalization.CultureInfo.InvariantCulture, out parsed))
        {
        }
        else
        {
            throw ProtocolError("provider_media_metadata_invalid", "Provider media metadata was invalid.");
        }

        if (parsed <= 0)
            throw ProtocolError("provider_media_metadata_invalid", "Provider media metadata was invalid.");
        return parsed;
    }

    public static void ValidateSid(string value, string prefix, string code)
    {
        if (value.Length != SidLength || !value.StartsWith(prefix, StringComparison.Ordinal))
            throw ProtocolError(code, "Provider media identifier was invalid.");
        for (int index = prefix.Length; index < value.Length; index++)
        {
            char character = value[index];
            if (character is not (>= '0' and <= '9')
                and not (>= 'A' and <= 'Z')
                and not (>= 'a' and <= 'z'))
                throw ProtocolError(code, "Provider media identifier was invalid.");
        }
    }

    public static void RequireMatchingStreamSid(JsonElement root, string expectedStreamSid)
    {
        string streamSid = RequireString(root, "streamSid", SidLength);
        if (!string.Equals(streamSid, expectedStreamSid, StringComparison.Ordinal))
            throw ProtocolError("provider_media_stream_mismatch", "Provider media stream did not match the active session.");
    }

    public static TwilioRealtimeAudioException ProtocolError(string code, string message) => new(code, message);
}
