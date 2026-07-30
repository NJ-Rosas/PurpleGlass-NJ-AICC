using System.Buffers.Binary;

namespace PurpleGlass.Adapters.Telephony.Twilio;

public sealed class StreamingPcm16Resampler
{
    private const int KernelRadius = 16;
    private readonly int sourceRateHz;
    private readonly int targetRateHz;
    private readonly List<short> sourceSamples = [];
    private int nextTargetSample;
    private byte incompleteSampleByte;
    private bool hasIncompleteSample;
    private bool completed;

    public StreamingPcm16Resampler(int sourceRateHz, int targetRateHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetRateHz);
        this.sourceRateHz = sourceRateHz;
        this.targetRateHz = targetRateHz;
    }

    public int SourceSampleCount => sourceSamples.Count;

    public int OutputSampleCount => nextTargetSample;

    public byte[] Convert(ReadOnlySpan<byte> pcm16LittleEndian, bool isFinal)
    {
        if (completed) throw new InvalidOperationException("The streaming resampler has already completed.");

        int offset = 0;
        if (hasIncompleteSample && pcm16LittleEndian.Length > 0)
        {
            sourceSamples.Add((short)(incompleteSampleByte | (pcm16LittleEndian[0] << 8)));
            hasIncompleteSample = false;
            offset = 1;
        }

        int completeBytes = (pcm16LittleEndian.Length - offset) & ~1;
        for (int index = offset; index < offset + completeBytes; index += sizeof(short))
            sourceSamples.Add(BinaryPrimitives.ReadInt16LittleEndian(pcm16LittleEndian[index..]));

        if (offset + completeBytes < pcm16LittleEndian.Length)
        {
            incompleteSampleByte = pcm16LittleEndian[^1];
            hasIncompleteSample = true;
        }

        if (isFinal && hasIncompleteSample)
            throw new TwilioRealtimeAudioException(
                "provider_media_pcm_invalid", "PCM audio ended with an incomplete sample.");

        int finalTargetCount = isFinal
            ? checked((int)Math.Round(
                sourceSamples.Count * (double)targetRateHz / sourceRateHz,
                MidpointRounding.AwayFromZero))
            : int.MaxValue;
        var output = new List<short>();
        while (nextTargetSample < finalTargetCount && CanProduce(nextTargetSample, isFinal))
        {
            output.Add(Resample(nextTargetSample));
            nextTargetSample++;
        }

        if (isFinal) completed = true;
        var bytes = new byte[checked(output.Count * sizeof(short))];
        for (int index = 0; index < output.Count; index++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * sizeof(short)), output[index]);
        return bytes;
    }

    private bool CanProduce(int targetIndex, bool isFinal)
    {
        if (sourceSamples.Count == 0) return false;
        if (sourceRateHz == targetRateHz) return targetIndex < sourceSamples.Count;
        if (isFinal) return true;
        double sourcePosition = targetIndex * (sourceRateHz / (double)targetRateHz);
        return (int)Math.Ceiling(sourcePosition) + KernelRadius < sourceSamples.Count;
    }

    private short Resample(int targetIndex)
    {
        if (sourceRateHz == targetRateHz) return sourceSamples[targetIndex];
        double sourcePosition = targetIndex * (sourceRateHz / (double)targetRateHz);
        int firstSample = Math.Max(0, (int)Math.Floor(sourcePosition) - KernelRadius);
        int lastSample = Math.Min(sourceSamples.Count - 1, (int)Math.Ceiling(sourcePosition) + KernelRadius);
        double cutoff = 0.45 * Math.Min(1d, targetRateHz / (double)sourceRateHz);
        double weightedSamples = 0;
        double totalWeight = 0;

        for (int sourceIndex = firstSample; sourceIndex <= lastSample; sourceIndex++)
        {
            double distance = sourceIndex - sourcePosition;
            if (Math.Abs(distance) > KernelRadius) continue;
            double sincArgument = 2 * cutoff * distance;
            double sinc = sincArgument == 0
                ? 1
                : Math.Sin(Math.PI * sincArgument) / (Math.PI * sincArgument);
            double window = 0.54 + (0.46 * Math.Cos(Math.PI * distance / KernelRadius));
            double weight = 2 * cutoff * sinc * window;
            weightedSamples += sourceSamples[sourceIndex] * weight;
            totalWeight += weight;
        }

        int interpolated = totalWeight == 0
            ? 0
            : (int)Math.Round(weightedSamples / totalWeight, MidpointRounding.AwayFromZero);
        return (short)Math.Clamp(interpolated, short.MinValue, short.MaxValue);
    }
}
