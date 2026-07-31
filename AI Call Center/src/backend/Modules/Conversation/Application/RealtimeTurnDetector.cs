using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PurpleGlass.Modules.Conversation.Application;

public sealed class RealtimeTurnDetector(RealtimeVoiceOptions options) : IDisposable
{
    private const double NoiseFloorAlpha = 0.05;
    private const double MinimumZeroCrossingRatio = 0.01;
    private const double MaximumZeroCrossingRatio = 0.45;
    private const double MinimumCrestFactor = 1.10;

    private readonly MemoryStream buffer = new();
    private readonly MemoryStream candidate = new();
    private AudioFormat? format;
    private AudioFormat? candidateFormat;
    private long firstSequence;
    private long lastSequence = -1;
    private long candidateFirstSequence;
    private long candidateLastSequence;
    private DateTimeOffset startedAtUtc;
    private DateTimeOffset lastFrameAtUtc;
    private DateTimeOffset candidateStartedAtUtc;
    private DateTimeOffset lastSpeechAtUtc;
    private TimeSpan trailingSilence;
    private TimeSpan utteranceDuration;
    private TimeSpan qualifiedSpeechDuration;
    private TimeSpan silenceDuration;
    private TimeSpan candidateDuration;
    private TimeSpan candidateSpeechLikeDuration;
    private double noiseFloor;
    private double candidateEnergyTotal;
    private double utteranceEnergyTotal;
    private bool speechActive;
    private int candidateFrames;
    private int candidateSpeechLikeFrames;
    private int inboundFrames;

    public bool IsSpeechActive => speechActive;
    public TimeSpan SilenceDuration => silenceDuration;
    public double NoiseFloor => noiseFloor;

    public TurnDetectionResult Push(RealtimeAudioFrame frame)
    {
        if (frame.Sequence <= lastSequence)
            return new(false, null, true);

        lastSequence = frame.Sequence;
        frame.Format.Validate();
        TimeSpan frameDuration = Duration(frame);
        FrameMetrics metrics = Analyze(frame);
        bool explicitSyntheticSpeech = frame.Format == AudioFormat.SyntheticText && frame.SpeechStarted;
        bool candidateStarted = false;
        bool speechJustConfirmed = false;

        if (!speechActive)
        {
            double threshold = candidateFrames == 0 ? StartThreshold() : ContinuationThreshold();
            bool energyQualified = explicitSyntheticSpeech || metrics.Energy >= threshold;
            if (!energyQualified)
            {
                RejectedVoiceCandidate? rejected = RejectCandidate();
                if (rejected is not null) ObserveNoise(rejected.EnergyMetric);
                ResetCandidate();
                ObserveNoise(metrics.Energy);
                silenceDuration += frameDuration;
                return new(false, null, false, rejected);
            }

            candidateStarted = candidateFrames == 0;
            AppendCandidate(frame, frameDuration, metrics, explicitSyntheticSpeech);
            bool qualified = explicitSyntheticSpeech || CandidateIsQualified();
            if (!qualified)
            {
                if (frame.EndOfUtterance || candidateDuration >= options.MaximumSpeechCandidateDuration)
                {
                    RejectedVoiceCandidate rejected = RejectCandidate()!;
                    ObserveNoise(rejected.EnergyMetric);
                    ResetCandidate();
                    return new(false, null, false, rejected, candidateStarted);
                }
                return new(false, null, false, null, candidateStarted);
            }

            ActivateCandidate();
            speechJustConfirmed = true;
        }

        bool containsSpeech = explicitSyntheticSpeech || metrics.Energy >= ContinuationThreshold();
        if (format != frame.Format)
            throw new InvalidOperationException("Audio format changed during an utterance.");
        if (buffer.Length + (speechJustConfirmed ? 0 : frame.Audio.Length) > options.MaximumAudioBytesPerUtterance)
            throw new InvalidOperationException("The caller utterance exceeded the configured audio limit.");

        if (!speechJustConfirmed)
        {
            AppendActiveFrame(frame, frameDuration, metrics, containsSpeech);
        }

        if (!frame.EndOfUtterance
            && trailingSilence < options.EndOfUtteranceSilence
            && utteranceDuration < options.MaximumUtteranceDuration)
            return new(speechJustConfirmed, null, false, null, candidateStarted);

        FinalizedVoiceUtterance utterance = FinalizeUtterance();
        return new(speechJustConfirmed, utterance, false, null, candidateStarted);
    }

    public FinalizedVoiceUtterance? Flush()
    {
        if (!speechActive || buffer.Length == 0) return null;
        return FinalizeUtterance();
    }

    private bool CandidateIsQualified()
    {
        if (candidateDuration < options.MinimumSpeechDuration || candidateFrames == 0) return false;
        double ratio = candidateSpeechLikeFrames / (double)candidateFrames;
        return ratio >= options.MinimumSpeechLikeFrameRatio;
    }

    private static FrameMetrics Analyze(RealtimeAudioFrame frame)
    {
        if (frame.Format == AudioFormat.SyntheticText)
            return new(frame.Audio.Length > 0 ? short.MaxValue : 0, short.MaxValue, 0.1, true);
        if (!string.Equals(frame.Format.Encoding, "audio/pcm", StringComparison.OrdinalIgnoreCase)
            || frame.Format.BitsPerSample != 16 || frame.Audio.Length < sizeof(short))
            return FrameMetrics.Silence;

        ReadOnlySpan<byte> bytes = frame.Audio.Span;
        long sumSquares = 0;
        int peak = 0;
        int zeroCrossings = 0;
        int samples = bytes.Length / sizeof(short);
        int previousSign = 0;
        for (int index = 0; index + 1 < bytes.Length; index += sizeof(short))
        {
            int sample = BinaryPrimitives.ReadInt16LittleEndian(bytes[index..]);
            int magnitude = Math.Abs(sample);
            sumSquares += (long)sample * sample;
            peak = Math.Max(peak, magnitude);
            int sign = Math.Sign(sample);
            if (sign != 0)
            {
                if (previousSign != 0 && sign != previousSign) zeroCrossings++;
                previousSign = sign;
            }
        }

        double energy = Math.Sqrt(sumSquares / (double)Math.Max(1, samples));
        double crossingRatio = zeroCrossings / (double)Math.Max(1, samples - 1);
        double crestFactor = energy == 0 ? 0 : peak / energy;
        bool speechLike = crossingRatio is >= MinimumZeroCrossingRatio and <= MaximumZeroCrossingRatio
            && crestFactor >= MinimumCrestFactor;
        return new(energy, peak, crossingRatio, speechLike);
    }

    private double StartThreshold() => Math.Max(
        options.SpeechEnergyThreshold,
        noiseFloor * options.SpeechStartSnrMultiplier);

    private double ContinuationThreshold() => Math.Max(
        options.SpeechEnergyThreshold * options.SpeechContinuationThresholdRatio,
        noiseFloor * options.SpeechStartSnrMultiplier * options.SpeechContinuationThresholdRatio);

    private void ObserveNoise(double energy)
    {
        if (energy < 0 || double.IsNaN(energy) || double.IsInfinity(energy)) return;
        noiseFloor = noiseFloor == 0
            ? Math.Min(energy, options.SpeechEnergyThreshold / options.SpeechStartSnrMultiplier)
            : (noiseFloor * (1 - NoiseFloorAlpha)) + (energy * NoiseFloorAlpha);
    }

    private static TimeSpan Duration(RealtimeAudioFrame frame)
    {
        if (frame.Format == AudioFormat.SyntheticText) return TimeSpan.FromMilliseconds(20);
        int bytesPerSample = Math.Max(1, frame.Format.BitsPerSample / 8);
        double samples = frame.Audio.Length / (double)(bytesPerSample * frame.Format.Channels);
        return TimeSpan.FromSeconds(samples / frame.Format.SampleRateHz);
    }

    private void AppendCandidate(
        RealtimeAudioFrame frame,
        TimeSpan duration,
        FrameMetrics metrics,
        bool explicitSyntheticSpeech)
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
        candidateEnergyTotal += metrics.Energy;
        if (metrics.SpeechLike || explicitSyntheticSpeech)
        {
            candidateSpeechLikeFrames++;
            candidateSpeechLikeDuration += duration;
        }
        lastFrameAtUtc = frame.ReceivedAtUtc;
    }

    private void ActivateCandidate()
    {
        DateTimeOffset candidateLastFrameAtUtc = lastFrameAtUtc;
        ResetBuffer();
        speechActive = true;
        format = candidateFormat;
        firstSequence = candidateFirstSequence;
        startedAtUtc = candidateStartedAtUtc;
        utteranceDuration = candidateDuration;
        qualifiedSpeechDuration = candidateSpeechLikeDuration;
        inboundFrames = candidateFrames;
        utteranceEnergyTotal = candidateEnergyTotal;
        candidate.Position = 0;
        candidate.CopyTo(buffer);
        lastFrameAtUtc = candidateLastFrameAtUtc;
        silenceDuration = TimeSpan.Zero;
        trailingSilence = TimeSpan.Zero;
        lastSpeechAtUtc = candidateLastFrameAtUtc;
        ResetCandidate();
    }

    private void AppendActiveFrame(
        RealtimeAudioFrame frame,
        TimeSpan duration,
        FrameMetrics metrics,
        bool containsSpeech)
    {
        buffer.Write(frame.Audio.Span);
        lastFrameAtUtc = frame.ReceivedAtUtc;
        utteranceDuration += duration;
        inboundFrames++;
        utteranceEnergyTotal += metrics.Energy;
        if (containsSpeech)
        {
            lastSpeechAtUtc = frame.ReceivedAtUtc;
            if (metrics.SpeechLike) qualifiedSpeechDuration += duration;
        }
        trailingSilence = containsSpeech ? TimeSpan.Zero : trailingSilence + duration;
    }

    private FinalizedVoiceUtterance FinalizeUtterance()
    {
        byte[] audio = buffer.ToArray();
        AudioFormat finalizedFormat = format
            ?? throw new InvalidOperationException("Utterance audio format is unavailable.");
        Guid turnId = DeterministicTurnId(firstSequence, lastSequence, audio);
        var utterance = new FinalizedVoiceUtterance(
            turnId, firstSequence, lastSequence, finalizedFormat, audio,
            startedAtUtc, lastFrameAtUtc == default ? startedAtUtc : lastFrameAtUtc,
            inboundFrames, utteranceDuration, qualifiedSpeechDuration,
            noiseFloor, utteranceEnergyTotal / Math.Max(1, inboundFrames),
            lastSpeechAtUtc == default ? lastFrameAtUtc : lastSpeechAtUtc);
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

    private RejectedVoiceCandidate? RejectCandidate() => candidateFrames == 0
        ? null
        : new RejectedVoiceCandidate(
            candidateFirstSequence,
            candidateLastSequence,
            candidateFrames,
            candidateDuration,
            candidateSpeechLikeDuration,
            noiseFloor,
            candidateEnergyTotal / candidateFrames,
            candidateDuration < options.MinimumSpeechDuration ? "speech_too_short" : "speech_not_voice_like");

    private void ResetBuffer()
    {
        buffer.SetLength(0);
        buffer.Position = 0;
        format = null;
        firstSequence = 0;
        startedAtUtc = default;
        lastSpeechAtUtc = default;
        lastFrameAtUtc = default;
        trailingSilence = TimeSpan.Zero;
        utteranceDuration = TimeSpan.Zero;
        qualifiedSpeechDuration = TimeSpan.Zero;
        utteranceEnergyTotal = 0;
        inboundFrames = 0;
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
        candidateSpeechLikeDuration = TimeSpan.Zero;
        candidateEnergyTotal = 0;
        candidateFrames = 0;
        candidateSpeechLikeFrames = 0;
    }

    public void Dispose()
    {
        buffer.Dispose();
        candidate.Dispose();
    }

    private readonly record struct FrameMetrics(
        double Energy,
        int Peak,
        double ZeroCrossingRatio,
        bool SpeechLike)
    {
        public static FrameMetrics Silence => new(0, 0, 0, false);
    }
}

public sealed record TurnDetectionResult(
    bool SpeechStarted,
    FinalizedVoiceUtterance? FinalizedUtterance,
    bool Duplicate,
    RejectedVoiceCandidate? RejectedCandidate = null,
    bool CandidateStarted = false);

public sealed record RejectedVoiceCandidate(
    long FirstSequence,
    long LastSequence,
    int InboundFrames,
    TimeSpan Duration,
    TimeSpan QualifiedSpeechDuration,
    double NoiseFloor,
    double EnergyMetric,
    string DiscardReason);
