namespace PurpleGlass.Adapters.Telephony.Twilio;

public sealed class TwilioRealtimeAudioOptions
{
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public int MaxJsonMessageBytes { get; init; } = 64 * 1024;

    public int MaxBase64PayloadCharacters { get; init; } = 32 * 1024;

    public int MaxDecodedMediaBytes { get; init; } = 24 * 1024;

    public int MaxPcmChunkBytes { get; init; } = 512 * 1024;

    public int MaxPcmResponseBytes { get; init; } = 16 * 1024 * 1024;

    public int MaxOutboundMediaBytes { get; init; } = 8 * 1024;

    public TimeSpan OutboundPacketDuration { get; init; } = TimeSpan.FromMilliseconds(100);

    public bool EnableOutboundPacing { get; init; } = true;

    public TwilioRealtimeAudioOptions Validate()
    {
        if (StartTimeout <= TimeSpan.Zero || StartTimeout > TimeSpan.FromSeconds(30))
            throw new InvalidOperationException("Twilio media start timeout must be between zero and thirty seconds.");
        if (CloseTimeout <= TimeSpan.Zero || CloseTimeout > TimeSpan.FromSeconds(10))
            throw new InvalidOperationException("Twilio media close timeout must be between zero and ten seconds.");
        if (MaxJsonMessageBytes is < 1_024 or > 1024 * 1024)
            throw new InvalidOperationException("Twilio media JSON messages must be bounded between 1 KiB and 1 MiB.");
        if (MaxBase64PayloadCharacters is < 256 or > 768 * 1024)
            throw new InvalidOperationException("Twilio media payload text has an invalid bound.");
        if (MaxDecodedMediaBytes is < 160 or > 512 * 1024)
            throw new InvalidOperationException("Twilio decoded media has an invalid bound.");
        if (MaxPcmChunkBytes is < 320 or > 4 * 1024 * 1024)
            throw new InvalidOperationException("Twilio PCM chunks have an invalid bound.");
        if (MaxPcmResponseBytes < MaxPcmChunkBytes || MaxPcmResponseBytes > 64 * 1024 * 1024)
            throw new InvalidOperationException("Twilio PCM responses have an invalid bound.");
        if (MaxOutboundMediaBytes < 80 || MaxOutboundMediaBytes > MaxDecodedMediaBytes)
            throw new InvalidOperationException("Twilio outbound media has an invalid bound.");
        if (OutboundPacketDuration < TimeSpan.FromMilliseconds(20)
            || OutboundPacketDuration > TimeSpan.FromMilliseconds(250))
            throw new InvalidOperationException("Twilio outbound packet duration must be between 20 and 250 milliseconds.");
        int pacedMediaBytes = checked((int)Math.Round(
            TwilioRealtimeAudioProtocol.TelephonySampleRate * OutboundPacketDuration.TotalSeconds,
            MidpointRounding.AwayFromZero));
        if (pacedMediaBytes < 80 || pacedMediaBytes > MaxOutboundMediaBytes)
            throw new InvalidOperationException("Twilio paced media must fit the outbound media bound.");

        int maximumEncodedLength = checked(((MaxOutboundMediaBytes + 2) / 3) * 4 + 512);
        if (maximumEncodedLength > MaxJsonMessageBytes)
            throw new InvalidOperationException("Twilio outbound media cannot fit within the configured JSON message bound.");
        if (MaxBase64PayloadCharacters > MaxJsonMessageBytes)
            throw new InvalidOperationException("Twilio media payload text cannot exceed the JSON message bound.");
        return this;
    }
}
