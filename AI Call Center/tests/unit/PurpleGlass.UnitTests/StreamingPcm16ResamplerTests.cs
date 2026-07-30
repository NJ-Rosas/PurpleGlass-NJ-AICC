using System.Buffers.Binary;
using PurpleGlass.Adapters.Telephony.Twilio;

namespace PurpleGlass.UnitTests;

public sealed class StreamingPcm16ResamplerTests
{
    [Fact]
    public void ArbitraryOddChunksMatchContiguousConversionExactly()
    {
        byte[] input = CompositeSignal();
        byte[] contiguous = Convert(input, input.Length);
        byte[] chunked = Convert(input, 1, 31, 2, 799, 1601, 17, 4095, 3, 8000);

        Assert.Equal(contiguous, chunked);
        Assert.Equal((int)Math.Round(input.Length / 2d / 3d, MidpointRounding.AwayFromZero) * 2,
            chunked.Length);
    }

    [Fact]
    public void IndependentResponsesDoNotShareFilterState()
    {
        byte[] first = SinePcm(1_000, 0.08, 14_000);
        byte[] second = SinePcm(2_100, 0.06, 9_000);

        _ = Convert(first, 1, 7, 93, 701);
        byte[] afterFirst = Convert(second, 5, 11, 37, 509);
        byte[] isolated = Convert(second, second.Length);

        Assert.Equal(isolated, afterFirst);
    }

    [Fact]
    public void DownsamplingPreservesVoiceBandAndAttenuatesAliasing()
    {
        byte[] voice = Convert(SinePcm(1_000, 0.20, 12_000), 13, 477, 901);
        byte[] ultrasonic = Convert(SinePcm(10_000, 0.20, 12_000), 17, 503, 997);

        double voiceRms = Rms(voice, 32);
        double ultrasonicRms = Rms(ultrasonic, 32);

        Assert.InRange(voiceRms, 7_000, 13_000);
        Assert.True(ultrasonicRms < voiceRms * 0.08,
            $"Expected out-of-band attenuation; voice RMS={voiceRms:F1}, out-of-band RMS={ultrasonicRms:F1}.");
    }

    [Fact]
    public void FinalOddByteIsRejectedAndCompletedInstanceCannotBeReused()
    {
        var invalid = new StreamingPcm16Resampler(24_000, 8_000);
        TwilioRealtimeAudioException exception = Assert.Throws<TwilioRealtimeAudioException>(
            () => invalid.Convert([0x01], isFinal: true));
        Assert.Equal("provider_media_pcm_invalid", exception.Code);

        var completed = new StreamingPcm16Resampler(24_000, 8_000);
        _ = completed.Convert([0, 0], isFinal: true);
        Assert.Throws<InvalidOperationException>(() => completed.Convert([], isFinal: false));
    }

    private static byte[] Convert(byte[] input, params int[] chunkPattern)
    {
        var resampler = new StreamingPcm16Resampler(24_000, 8_000);
        var output = new List<byte>();
        int offset = 0;
        int patternIndex = 0;
        while (offset < input.Length)
        {
            int requested = chunkPattern[patternIndex++ % chunkPattern.Length];
            int length = Math.Min(requested, input.Length - offset);
            bool final = offset + length == input.Length;
            output.AddRange(resampler.Convert(input.AsSpan(offset, length), final));
            offset += length;
        }
        return output.ToArray();
    }

    private static byte[] CompositeSignal()
    {
        byte[] result = new byte[24_000];
        SinePcm(850, 0.08, 11_000).CopyTo(result, 1_200);
        SinePcm(6_000, 0.04, 7_000).CopyTo(result, 9_600);
        SinePcm(1_900, 0.06, 9_000).CopyTo(result, 16_800);
        return result;
    }

    private static byte[] SinePcm(double frequency, double seconds, double amplitude)
    {
        int sampleCount = (int)Math.Round(24_000 * seconds, MidpointRounding.AwayFromZero);
        var result = new byte[sampleCount * 2];
        for (int index = 0; index < sampleCount; index++)
        {
            short value = (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * frequency * index / 24_000));
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(index * 2), value);
        }
        return result;
    }

    private static double Rms(byte[] pcm, int edgeSamples)
    {
        int count = pcm.Length / 2;
        double sum = 0;
        int included = 0;
        for (int index = edgeSamples; index < count - edgeSamples; index++)
        {
            short value = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(index * 2));
            sum += value * (double)value;
            included++;
        }
        return Math.Sqrt(sum / included);
    }
}
