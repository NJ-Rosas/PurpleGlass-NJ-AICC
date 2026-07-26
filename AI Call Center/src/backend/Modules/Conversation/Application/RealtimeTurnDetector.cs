using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PurpleGlass.Modules.Conversation.Application;

public sealed class RealtimeTurnDetector(RealtimeVoiceOptions options) : IDisposable
{
    private readonly MemoryStream buffer = new();
    private AudioFormat? format;
    private long firstSequence;
    private long lastSequence = -1;
    private DateTimeOffset startedAtUtc;
    private DateTimeOffset lastFrameAtUtc;
    private TimeSpan trailingSilence;
    private TimeSpan utteranceDuration;
    private TimeSpan silenceDuration;
    private bool speechActive;

    public bool IsSpeechActive => speechActive;
    public TimeSpan SilenceDuration => silenceDuration;

    public TurnDetectionResult Push(RealtimeAudioFrame frame)
    {
        if (frame.Sequence <= lastSequence)
            return new(false, null, true);

        lastSequence = frame.Sequence;
        frame.Format.Validate();
        bool containsSpeech = frame.SpeechStarted || HasSpeech(frame);
        TimeSpan frameDuration = Duration(frame);
        bool speechStarted = false;

        if (containsSpeech && !speechActive)
        {
            ResetBuffer();
            speechActive = true;
            speechStarted = true;
            format = frame.Format;
            firstSequence = frame.Sequence;
            startedAtUtc = frame.ReceivedAtUtc;
            silenceDuration = TimeSpan.Zero;
        }

        if (!speechActive)
        {
            silenceDuration += frameDuration;
            return new(false, null, false);
        }

        if (format != frame.Format)
            throw new InvalidOperationException("Audio format changed during an utterance.");
        if (buffer.Length + frame.Audio.Length > options.MaximumAudioBytesPerUtterance)
            throw new InvalidOperationException("The caller utterance exceeded the configured audio limit.");

        buffer.Write(frame.Audio.Span);
        lastFrameAtUtc = frame.ReceivedAtUtc;
        utteranceDuration += frameDuration;
        trailingSilence = containsSpeech ? TimeSpan.Zero : trailingSilence + frameDuration;

        if (!frame.EndOfUtterance
            && trailingSilence < options.EndOfUtteranceSilence
            && utteranceDuration < options.MaximumUtteranceDuration)
            return new(speechStarted, null, false);

        FinalizedVoiceUtterance utterance = FinalizeUtterance();
        return new(speechStarted, utterance, false);
    }

    public FinalizedVoiceUtterance? Flush()
    {
        if (!speechActive || buffer.Length == 0) return null;
        return FinalizeUtterance();
    }

    private bool HasSpeech(RealtimeAudioFrame frame)
    {
        if (frame.Format == AudioFormat.SyntheticText)
            return frame.Audio.Length > 0;
        if (!string.Equals(frame.Format.Encoding, "audio/pcm", StringComparison.OrdinalIgnoreCase)
            || frame.Format.BitsPerSample != 16 || frame.Audio.Length < 2)
            return false;

        ReadOnlySpan<byte> bytes = frame.Audio.Span;
        long total = 0;
        int samples = bytes.Length / 2;
        for (int index = 0; index + 1 < bytes.Length; index += 2)
            total += Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(bytes[index..]));
        return total / Math.Max(1, samples) >= options.SpeechEnergyThreshold;
    }

    private static TimeSpan Duration(RealtimeAudioFrame frame)
    {
        if (frame.Format == AudioFormat.SyntheticText) return TimeSpan.FromMilliseconds(20);
        int bytesPerSample = Math.Max(1, frame.Format.BitsPerSample / 8);
        double samples = frame.Audio.Length / (double)(bytesPerSample * frame.Format.Channels);
        return TimeSpan.FromSeconds(samples / frame.Format.SampleRateHz);
    }

    private FinalizedVoiceUtterance FinalizeUtterance()
    {
        byte[] audio = buffer.ToArray();
        AudioFormat finalizedFormat = format ?? throw new InvalidOperationException("Utterance audio format is unavailable.");
        Guid turnId = DeterministicTurnId(firstSequence, lastSequence, audio);
        var utterance = new FinalizedVoiceUtterance(
            turnId, firstSequence, lastSequence, finalizedFormat, audio,
            startedAtUtc, lastFrameAtUtc == default ? startedAtUtc : lastFrameAtUtc);
        ResetBuffer();
        speechActive = false;
        return utterance;
    }

    private static Guid DeterministicTurnId(long first, long last, ReadOnlySpan<byte> audio)
    {
        byte[] prefix = Encoding.UTF8.GetBytes($"voice:{first}:{last}:");
        byte[] material = new byte[prefix.Length + audio.Length];
        prefix.CopyTo(material, 0);
        audio.CopyTo(material.AsSpan(prefix.Length));
        return new Guid(SHA256.HashData(material)[..16]);
    }

    private void ResetBuffer()
    {
        buffer.SetLength(0);
        format = null;
        firstSequence = 0;
        startedAtUtc = default;
        lastFrameAtUtc = default;
        trailingSilence = TimeSpan.Zero;
        utteranceDuration = TimeSpan.Zero;
    }

    public void Dispose() => buffer.Dispose();
}

public sealed record TurnDetectionResult(
    bool SpeechStarted,
    FinalizedVoiceUtterance? FinalizedUtterance,
    bool Duplicate);
