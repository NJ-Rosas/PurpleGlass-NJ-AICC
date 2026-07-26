using System.Net.WebSockets;
using System.Text.Json;
using PurpleGlass.Modules.Conversation.Application;

namespace PurpleGlass.Adapters.Telephony.Twilio;

public sealed record TwilioMediaStartMetadata(
    string AccountSid,
    string CallSid,
    string StreamSid,
    string ProtocolVersion,
    AudioFormat InputFormat);

public sealed class TwilioRealtimeAudioTransportFactory(TimeProvider timeProvider)
{
    public TwilioRealtimeAudioTransportFactory() : this(TimeProvider.System)
    {
    }

    public async Task<TwilioRealtimeAudioTransport> InitializeAsync(
        WebSocket socket,
        string expectedAccountSid,
        TwilioRealtimeAudioOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        TwilioRealtimeAudioProtocol.ValidateSid(
            expectedAccountSid,
            "AC",
            "provider_account_invalid");
        if (socket.State != WebSocketState.Open)
            throw new TwilioRealtimeAudioException("provider_media_not_open", "Provider media connection was not open.");

        using var startSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startSource.CancelAfter(options.StartTimeout);
        try
        {
            using TwilioJsonMessage connected = await TwilioRealtimeAudioProtocol.ReceiveMessageAsync(
                socket, options.MaxJsonMessageBytes, startSource.Token)
                ?? throw new TwilioRealtimeAudioException(
                    "provider_media_disconnected", "Provider media connection ended before initialization.");
            string protocolVersion = ValidateConnected(connected.Document.RootElement);

            using TwilioJsonMessage start = await TwilioRealtimeAudioProtocol.ReceiveMessageAsync(
                socket, options.MaxJsonMessageBytes, startSource.Token)
                ?? throw new TwilioRealtimeAudioException(
                    "provider_media_disconnected", "Provider media connection ended before initialization.");
            TwilioMediaStartMetadata metadata = ValidateStart(
                start.Document.RootElement,
                expectedAccountSid,
                protocolVersion);
            return new TwilioRealtimeAudioTransport(socket, metadata, options, timeProvider);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            await RejectAsync(socket, options, "media_start_timeout");
            throw new TwilioRealtimeAudioException(
                "provider_media_start_timeout", "Provider media initialization timed out.", exception);
        }
        catch (TwilioRealtimeAudioException)
        {
            await RejectAsync(socket, options, "media_rejected");
            throw;
        }
    }

    private static string ValidateConnected(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !string.Equals(TwilioRealtimeAudioProtocol.RequireString(root, "event", 20), "connected", StringComparison.Ordinal)
            || !string.Equals(TwilioRealtimeAudioProtocol.RequireString(root, "protocol", 20), "Call", StringComparison.Ordinal))
            throw new TwilioRealtimeAudioException(
                "provider_media_connected_invalid", "Provider media connection preamble was invalid.");
        string version = TwilioRealtimeAudioProtocol.RequireString(root, "version", 20);
        if (!string.Equals(version, "1.0.0", StringComparison.Ordinal))
            throw new TwilioRealtimeAudioException(
                "provider_media_protocol_unsupported", "Provider media protocol version was unsupported.");
        return version;
    }

    private static TwilioMediaStartMetadata ValidateStart(
        JsonElement root,
        string expectedAccountSid,
        string protocolVersion)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !string.Equals(TwilioRealtimeAudioProtocol.RequireString(root, "event", 20), "start", StringComparison.Ordinal))
            throw new TwilioRealtimeAudioException(
                "provider_media_start_invalid", "Provider media start event was invalid.");
        _ = TwilioRealtimeAudioProtocol.RequirePositiveInt64(root, "sequenceNumber");

        JsonElement start = TwilioRealtimeAudioProtocol.RequireObject(root, "start");
        string accountSid = TwilioRealtimeAudioProtocol.RequireString(start, "accountSid", TwilioRealtimeAudioProtocol.SidLength);
        string callSid = TwilioRealtimeAudioProtocol.RequireString(start, "callSid", TwilioRealtimeAudioProtocol.SidLength);
        string streamSid = TwilioRealtimeAudioProtocol.RequireString(start, "streamSid", TwilioRealtimeAudioProtocol.SidLength);
        TwilioRealtimeAudioProtocol.ValidateSid(accountSid, "AC", "provider_account_invalid");
        TwilioRealtimeAudioProtocol.ValidateSid(callSid, "CA", "provider_call_invalid");
        TwilioRealtimeAudioProtocol.ValidateSid(streamSid, "MZ", "provider_media_stream_invalid");
        if (!string.Equals(accountSid, expectedAccountSid, StringComparison.Ordinal))
            throw new TwilioRealtimeAudioException(
                "provider_account_mismatch", "Provider account did not match the configured account.");
        TwilioRealtimeAudioProtocol.RequireMatchingStreamSid(root, streamSid);
        ValidateInboundTrack(start);

        JsonElement mediaFormat = TwilioRealtimeAudioProtocol.RequireObject(start, "mediaFormat");
        string encoding = TwilioRealtimeAudioProtocol.RequireString(mediaFormat, "encoding", 50);
        int sampleRate = TwilioRealtimeAudioProtocol.RequireInt32(mediaFormat, "sampleRate", 1, 192_000);
        int channels = TwilioRealtimeAudioProtocol.RequireInt32(mediaFormat, "channels", 1, 2);
        if (!string.Equals(encoding, TwilioRealtimeAudioProtocol.SourceEncoding, StringComparison.Ordinal)
            || sampleRate != TwilioRealtimeAudioProtocol.TelephonySampleRate
            || channels != TwilioRealtimeAudioProtocol.TelephonyChannels)
            throw new TwilioRealtimeAudioException(
                "provider_media_format_unsupported", "Provider media format was unsupported.");

        // Custom parameters are deliberately not materialized or trusted. Tenant and location scope
        // must be resolved by the host from the authenticated provider CallSid.
        return new(accountSid, callSid, streamSid, protocolVersion, AudioFormat.Pcm16());
    }

    private static void ValidateInboundTrack(JsonElement start)
    {
        if (!start.TryGetProperty("tracks", out JsonElement tracks) || tracks.ValueKind != JsonValueKind.Array)
            throw new TwilioRealtimeAudioException(
                "provider_media_track_invalid", "Provider media track metadata was invalid.");
        int count = 0;
        bool inbound = false;
        foreach (JsonElement track in tracks.EnumerateArray())
        {
            count++;
            if (count > 4 || track.ValueKind != JsonValueKind.String)
                throw new TwilioRealtimeAudioException(
                    "provider_media_track_invalid", "Provider media track metadata was invalid.");
            inbound |= string.Equals(track.GetString(), "inbound", StringComparison.Ordinal);
        }
        if (!inbound)
            throw new TwilioRealtimeAudioException(
                "provider_media_track_invalid", "Provider media did not contain an inbound track.");
    }

    private static async Task RejectAsync(
        WebSocket socket,
        TwilioRealtimeAudioOptions options,
        string reason)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        using var closeSource = new CancellationTokenSource(options.CloseTimeout);
        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, closeSource.Token);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        {
        }
    }
}
