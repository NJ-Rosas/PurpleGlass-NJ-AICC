using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PurpleGlass.Modules.Conversation.Application;

public sealed class RealtimeTurnDetector(RealtimeVoiceOptions options) : IDisposable
{
    private readonly MemoryStream buffer = new();
    private readonly MemoryStream candidate = new();
    private AudioFormat? format;
    private long firstSequence;
    private long lastSequence = -1;
    private DateTimeOffset startedAtUtc;
    private DateTimeOffset lastFrameAtUtc;
    private TimeSpan trailingSilence;
    private TimeSpan utteranceDuration;
    private TimeSpan silenceDuration;
    private TimeSpan candidateDuration;
    private bool speechActive;
    private AudioFormat? candidateFormat;
    private long candidateFirstSequence;
    private long candidateLastSequence;
    private DateTimeOffset candidateStartedAtUtc;
    private int candidateFrames;
    private int inboundFrames;

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

        if (!speechActive)
        {
            if (!containsSpeech)
            {
                RejectedVoiceCandidate? rejected = RejectCandidate();
                ResetCandidate();
                silenceDuration += frameDuration;
                return new(false, null, false, rejected);
            }

            AppendCandidate(frame, frameDuration);
            bool explicitSyntheticSpeech = frame.Format == AudioFormat.SyntheticText
                && frame.SpeechStarted;
            if (!explicitSyntheticSpeech && candidateDuration < options.MinimumSpeechDuration)
            {
                RejectedVoiceCandidate? rejected = null;
                if (frame.EndOfUtterance)
                {
                    rejected = RejectCandidate();
                    ResetCandidate();
                }
                return new(false, null, false, rejected);
            }

            ActivateCandidate();
            speechStarted = true;
        }

        if (format != frame.Format)
            throw new InvalidOperationException("Audio format changed during an utterance.");
        if (buffer.Length + frame.Audio.Length > options.MaximumAudioBytesPerUtterance)
            throw new InvalidOperationException("The caller utterance exceeded the configured audio limit.");

        if (!speechStarted)
        {
            buffer.Write(frame.Audio.Span);
            lastFrameAtUtc = frame.ReceivedAtUtc;
            utteranceDuration += frameDuration;
            inboundFrames++;
            trailingSilence = containsSpeech ? TimeSpan.Zero : trailingSilence + frameDuration;
        }

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
            startedAtUtc, lastFrameAtUtc == default ? startedAtUtc : lastFrameAtUtc,
            inboundFrames, utteranceDuration);
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
        buffer.Position = 0;
        format = null;
        firstSequence = 0;
        startedAtUtc = default;
        lastFrameAtUtc = default;
        trailingSilence = TimeSpan.Zero;
        utteranceDuration = TimeSpan.Zero;
        inboundFrames = 0;
    }

    private void AppendCandidate(RealtimeAudioFrame frame, TimeSpan duration)
    {
        if (candidateFormat is null)
        {
            candidateFormat = frame.Format;
            candidateFirstSequence = frame.Sequence;
            candidateStartedAtUtc = frame.ReceivedAtUtc;
        }
        else if (candidateFormat != frame.Format)
        {
            ResetCandidate();
            candidateFormat = frame.Format;
            candidateFirstSequence = frame.Sequence;
            candidateStartedAtUtc = frame.ReceivedAtUtc;
        }
        if (candidate.Length + frame.Audio.Length > options.MaximumAudioBytesPerUtterance)
            throw new InvalidOperationException("The caller speech candidate exceeded the configured audio limit.");
        candidate.Write(frame.Audio.Span);
        candidateLastSequence = frame.Sequence;
        candidateDuration += duration;
        candidateFrames++;
        lastFrameAtUtc = frame.ReceivedAtUtc;
    }

    private void ActivateCandidate()
    {
        ResetBuffer();
        speechActive = true;
        format = candidateFormat;
        firstSequence = candidateFirstSequence;
        startedAtUtc = candidateStartedAtUtc;
        utteranceDuration = candidateDuration;
        inboundFrames = candidateFrames;
        candidate.Position = 0;
        candidate.CopyTo(buffer);
        silenceDuration = TimeSpan.Zero;
        trailingSilence = TimeSpan.Zero;
        ResetCandidate();
    }

    private void ResetCandidate()
    {
        candidate.SetLength(0);
        candidate.Position = 0;
        candidateFormat = null;
        candidateFirstSequence = 0;
        candidateLastSequence = 0;
        candidateStartedAtUtc = default;
        candidateDuration = TimeSpan.Zero;
        candidateFrames = 0;
    }

    private RejectedVoiceCandidate? RejectCandidate() => candidateFrames == 0
        ? null
        : new RejectedVoiceCandidate(
            candidateFirstSequence,
            candidateLastSequence,
            candidateFrames,
            candidateDuration,
            "speech_too_short");

    public void Dispose()
    {
        buffer.Dispose();
        candidate.Dispose();
    }
}

public sealed record TurnDetectionResult(
    bool SpeechStarted,
    FinalizedVoiceUtterance? FinalizedUtterance,
    bool Duplicate,
    RejectedVoiceCandidate? RejectedCandidate = null);

public sealed record RejectedVoiceCandidate(
    long FirstSequence,
    long LastSequence,
    int InboundFrames,
    TimeSpan Duration,
    string DiscardReason);
