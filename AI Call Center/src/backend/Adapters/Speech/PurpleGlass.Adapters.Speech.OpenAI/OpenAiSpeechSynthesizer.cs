using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PurpleGlass.Modules.Conversation.Application;

namespace PurpleGlass.Adapters.Speech.OpenAI;

public sealed class OpenAiSpeechSynthesizer : ISpeechSynthesizer
{
    private const int OutputSampleRate = 24_000;
    private const int OutputChannels = 1;
    private const int OutputBitsPerSample = 16;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient httpClient;
    private readonly OpenAiSpeechOptions options;

    public OpenAiSpeechSynthesizer(HttpClient httpClient, OpenAiSpeechOptions options)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.options = (options ?? throw new ArgumentNullException(nameof(options))).Validate();
    }

    public string AdapterKey => "openai";

    public async Task<SpeechSynthesisResult> SynthesizeAsync(
        SpeechSynthesisRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        string text = request.Text.Trim();
        if (text.Length == 0 || text.Length > options.MaximumSynthesisCharacters)
        {
            return Failure(request.Voice.VoiceId,
                new RuntimeFailure("speech_synthesis_input_invalid", "The speech synthesis input is invalid.", false));
        }

        string voice = request.Voice.VoiceId.Trim();
        if (voice.Length == 0 || voice.Length > 100
            || voice.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
            || request.Voice.SpeakingRate is < 0.25m or > 4.0m)
        {
            return Failure(request.Voice.VoiceId,
                new RuntimeFailure("speech_synthesis_configuration_invalid", "The speech voice configuration is invalid.", false));
        }

        string? language = NormalizeLanguage(request.Language);
        if (language is null)
        {
            return Failure(voice,
                new RuntimeFailure("speech_synthesis_language_invalid", "The speech language configuration is invalid.", false));
        }

        string? instructions = options.SynthesisModel.StartsWith("gpt-4o-mini-tts", StringComparison.Ordinal)
            ? $"Speak in the configured {language} language with a {NormalizeStyle(request.Voice.Style)} delivery."
            : null;
        var payload = new OpenAiSpeechRequest(
            options.SynthesisModel,
            text,
            voice,
            "pcm",
            request.Voice.SpeakingRate,
            "audio",
            instructions);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, options.SpeechEndpoint)
        {
            Content = JsonContent.Create(payload, options: SerializerOptions),
        };
        OpenAiSpeechHttp.AddAuthentication(httpRequest, options.ApiKey);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/pcm"));

        try
        {
            using HttpResponseMessage response = await httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Failure(voice, OpenAiSpeechHttp.MapSynthesisFailure(response.StatusCode));
            }

            byte[] audio = await OpenAiSpeechHttp.ReadBoundedAsync(
                response.Content,
                options.MaximumSpeechResponseBytes,
                cancellationToken);
            if (audio.Length == 0 || audio.Length % 2 != 0 || HasContainerHeader(audio))
            {
                return Failure(voice,
                    new RuntimeFailure("speech_synthesis_response_invalid", "Speech synthesis returned invalid audio.", false));
            }

            AudioFormat format = AudioFormat.Pcm16(OutputSampleRate, OutputChannels);
            List<SynthesizedAudioChunk> chunks = Chunk(audio, format, options.AudioChunkBytes);
            string hash = Convert.ToHexString(SHA256.HashData(audio))[..16].ToLowerInvariant();
            double seconds = (double)audio.Length / (OutputSampleRate * OutputChannels * (OutputBitsPerSample / 8));
            return new SpeechSynthesisResult(
                $"openai://speech/{hash}",
                "audio/pcm",
                TimeSpan.FromSeconds(seconds),
                voice,
                new Dictionary<string, string>
                {
                    ["adapter"] = AdapterKey,
                    ["encoding"] = "pcm16le",
                    ["sample-rate-hz"] = OutputSampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["language"] = language,
                },
                AudioChunks: chunks);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure(voice,
                new RuntimeFailure("speech_synthesis_timeout", "Speech synthesis timed out.", true));
        }
        catch (HttpRequestException)
        {
            return Failure(voice,
                new RuntimeFailure("speech_synthesis_network_failed", "Speech synthesis is temporarily unavailable.", true));
        }
        catch (IOException)
        {
            return Failure(voice,
                new RuntimeFailure("speech_synthesis_network_failed", "Speech synthesis is temporarily unavailable.", true));
        }
        catch (OpenAiSpeechResponseTooLargeException)
        {
            return Failure(voice,
                new RuntimeFailure("speech_synthesis_response_too_large", "Speech synthesis exceeded the safe audio size.", false));
        }
    }

    private static List<SynthesizedAudioChunk> Chunk(
        byte[] audio,
        AudioFormat format,
        int chunkBytes)
    {
        var chunks = new List<SynthesizedAudioChunk>((audio.Length + chunkBytes - 1) / chunkBytes);
        long sequence = 1;
        for (int offset = 0; offset < audio.Length; offset += chunkBytes)
        {
            int length = Math.Min(chunkBytes, audio.Length - offset);
            byte[] payload = audio.AsSpan(offset, length).ToArray();
            chunks.Add(new SynthesizedAudioChunk(
                sequence++,
                format,
                payload,
                offset + length == audio.Length));
        }

        return chunks;
    }

    private static bool HasContainerHeader(ReadOnlySpan<byte> audio) =>
        audio.StartsWith("RIFF"u8)
        || audio.StartsWith("ID3"u8)
        || audio.StartsWith("OggS"u8)
        || audio.StartsWith("fLaC"u8);

    private static string? NormalizeLanguage(string value)
    {
        string normalized = value.Trim();
        if (normalized.Length is < 2 or > 35
            || normalized.Any(character => !(char.IsLetter(character) || character is '-' or '_')))
        {
            return null;
        }

        return normalized;
    }

    private static string NormalizeStyle(string value)
    {
        string normalized = value.Trim();
        return normalized.Length is > 0 and <= 50
            && normalized.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or ' ')
            ? normalized
            : "neutral";
    }

    private static SpeechSynthesisResult Failure(string voiceId, RuntimeFailure failure) => new(
        string.Empty,
        "application/octet-stream",
        null,
        voiceId,
        new Dictionary<string, string>(),
        failure);

    private sealed record OpenAiSpeechRequest(
        string Model,
        string Input,
        string Voice,
        string ResponseFormat,
        decimal Speed,
        string StreamFormat,
        string? Instructions);
}
