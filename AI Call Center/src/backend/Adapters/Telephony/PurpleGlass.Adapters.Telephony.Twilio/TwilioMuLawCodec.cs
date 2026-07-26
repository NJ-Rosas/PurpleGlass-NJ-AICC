using System.Buffers.Binary;

namespace PurpleGlass.Adapters.Telephony.Twilio;

internal static class TwilioMuLawCodec
{
    private const int Bias = 0x84;
    private const int Clip = 32_635;

    public static byte[] DecodeToPcm16(ReadOnlySpan<byte> source)
    {
        var destination = new byte[checked(source.Length * sizeof(short))];
        for (int index = 0; index < source.Length; index++)
            BinaryPrimitives.WriteInt16LittleEndian(destination.AsSpan(index * sizeof(short)), DecodeSample(source[index]));
        return destination;
    }

    public static byte[] EncodePcm16(ReadOnlySpan<byte> source)
    {
        if ((source.Length & 1) != 0)
            throw new TwilioRealtimeAudioException("provider_media_pcm_invalid", "PCM audio must contain complete samples.");

        var destination = new byte[source.Length / sizeof(short)];
        for (int index = 0; index < destination.Length; index++)
            destination[index] = EncodeSample(BinaryPrimitives.ReadInt16LittleEndian(source[(index * sizeof(short))..]));
        return destination;
    }

    public static byte[] ResamplePcm16Mono(ReadOnlySpan<byte> source, int sourceRateHz, int targetRateHz)
    {
        if ((source.Length & 1) != 0 || sourceRateHz <= 0 || targetRateHz <= 0)
            throw new TwilioRealtimeAudioException("provider_media_pcm_invalid", "PCM audio format is invalid.");
        if (sourceRateHz == targetRateHz) return source.ToArray();

        int sourceSamples = source.Length / sizeof(short);
        if (sourceSamples == 0) return [];
        int targetSamples = Math.Max(1, checked((int)Math.Round(
            sourceSamples * (double)targetRateHz / sourceRateHz,
            MidpointRounding.AwayFromZero)));
        var destination = new byte[checked(targetSamples * sizeof(short))];
        double sourceStep = sourceRateHz / (double)targetRateHz;
        double cutoff = 0.45 * Math.Min(1d, targetRateHz / (double)sourceRateHz);
        const int kernelRadius = 16;

        for (int index = 0; index < targetSamples; index++)
        {
            double sourcePosition = index * sourceStep;
            int firstSample = Math.Max(0, (int)Math.Floor(sourcePosition) - kernelRadius);
            int lastSample = Math.Min(sourceSamples - 1, (int)Math.Ceiling(sourcePosition) + kernelRadius);
            double weightedSamples = 0;
            double totalWeight = 0;

            for (int sourceIndex = firstSample; sourceIndex <= lastSample; sourceIndex++)
            {
                double distance = sourceIndex - sourcePosition;
                if (Math.Abs(distance) > kernelRadius) continue;
                double sincArgument = 2 * cutoff * distance;
                double sinc = sincArgument == 0
                    ? 1
                    : Math.Sin(Math.PI * sincArgument) / (Math.PI * sincArgument);
                double window = 0.54 + (0.46 * Math.Cos(Math.PI * distance / kernelRadius));
                double weight = 2 * cutoff * sinc * window;
                short sample = BinaryPrimitives.ReadInt16LittleEndian(source[(sourceIndex * sizeof(short))..]);
                weightedSamples += sample * weight;
                totalWeight += weight;
            }

            int interpolated = totalWeight == 0
                ? 0
                : (int)Math.Round(weightedSamples / totalWeight, MidpointRounding.AwayFromZero);
            BinaryPrimitives.WriteInt16LittleEndian(
                destination.AsSpan(index * sizeof(short)),
                (short)Math.Clamp(interpolated, short.MinValue, short.MaxValue));
        }

        return destination;
    }

    private static short DecodeSample(byte encoded)
    {
        int value = ~encoded & 0xff;
        int magnitude = ((value & 0x0f) << 3) + Bias;
        magnitude <<= (value & 0x70) >> 4;
        return (short)((value & 0x80) != 0 ? Bias - magnitude : magnitude - Bias);
    }

    private static byte EncodeSample(short sample)
    {
        int value = sample;
        int sign = (value >> 8) & 0x80;
        if (sign != 0) value = -value;
        value = Math.Min(value, Clip) + Bias;

        int exponent = 7;
        for (int mask = 0x4000; exponent > 0 && (value & mask) == 0; exponent--, mask >>= 1)
        {
        }

        int mantissa = (value >> (exponent + 3)) & 0x0f;
        return (byte)~(sign | (exponent << 4) | mantissa);
    }
}
