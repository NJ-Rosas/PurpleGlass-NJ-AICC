using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using System.ClientModel;
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
    private readonly IOpenAiSpeechStreamingGateway? streamingGateway;

    public OpenAiSpeechSynthesizer(
        HttpClient httpClient,
        OpenAiSpeechOptions options,
        IOpenAiSpeechStreamingGateway? streamingGateway = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.options = (options ?? throw new ArgumentNullException(nameof(options))).Validate();
        this.streamingGateway = streamingGateway;
    }

    public string AdapterKey => "openai";

    public async IAsyncEnumerable<SpeechSynthesisStreamUpdate> SynthesizeStreamingAsync(
        SpeechSynthesisRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (streamingGateway is null)
        {
            SpeechSynthesisResult buffered = await SynthesizeAsync(request, cancellationToken);
            if (buffered.Failure is null)
            {
                foreach (SynthesizedAudioChunk chunk in buffered.AudioChunks ?? [])
                    yield return SpeechSynthesisStreamUpdate.Audio(chunk);
            }
            yield return SpeechSynthesisStreamUpdate.Completed(buffered);
            yield break;
        }

        (string Text, string Voice, string Language, string? Instructions, RuntimeFailure? Failure) validated =
            ValidateStreamingRequest(request);
        if (validated.Failure is not null)
        {
            yield return SpeechSynthesisStreamUpdate.Completed(Failure(request.Voice.VoiceId, validated.Failure));
            yield break;
        }

        long sequence = 1;
        int totalBytes = 0;
        bool providerCompleted = false;
        int inputTokens = 0;
        int outputTokens = 0;
        RuntimeFailure? streamFailure = null;
        var prefix = new List<byte>(4);
        await using IAsyncEnumerator<OpenAiSpeechStreamUpdate> enumerator = streamingGateway.GenerateAsync(
            validated.Text,
            validated.Voice,
            validated.Instructions,
            request.Voice.SpeakingRate,
            cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (!providerCompleted && streamFailure is null)
        {
            bool available;
            try { available = await enumerator.MoveNextAsync(); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (ClientResultException exception)
            {
                streamFailure = OpenAiSpeechHttp.MapSynthesisFailure((System.Net.HttpStatusCode)exception.Status);
                break;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                streamFailure = new RuntimeFailure(
                    "speech_synthesis_network_failed", "Speech synthesis is temporarily unavailable.", true);
                break;
            }
            if (!available) break;

            OpenAiSpeechStreamUpdate update = enumerator.Current;
            if (update.Completed)
            {
                providerCompleted = true;
                inputTokens = update.InputTokens;
                outputTokens = update.OutputTokens;
                continue;
            }
            if (update.Audio.Length == 0) continue;
            byte[] ownedAudio = update.Audio.ToArray();
            totalBytes = checked(totalBytes + ownedAudio.Length);
            if (totalBytes > options.MaximumSpeechResponseBytes)
            {
                CryptographicOperations.ZeroMemory(ownedAudio);
                streamFailure = new RuntimeFailure(
                    "speech_synthesis_response_too_large", "Speech synthesis exceeded the safe audio size.", false);
                break;
            }
            foreach (byte value in ownedAudio.AsSpan(0, Math.Min(ownedAudio.Length, 4 - prefix.Count)))
                prefix.Add(value);
            yield return SpeechSynthesisStreamUpdate.Audio(new SynthesizedAudioChunk(
                sequence++, AudioFormat.Pcm16(OutputSampleRate, OutputChannels), ownedAudio, IsFinal: false));
        }

        if (streamFailure is null && (!providerCompleted || totalBytes == 0 || (totalBytes & 1) != 0
            || HasContainerHeader(prefix.ToArray())))
        {
            streamFailure = new RuntimeFailure(
                "speech_synthesis_response_invalid", "Speech synthesis returned invalid audio.", false);
        }
        if (streamFailure is not null)
        {
            yield return SpeechSynthesisStreamUpdate.Completed(Failure(validated.Voice, streamFailure));
            yield break;
        }

        yield return SpeechSynthesisStreamUpdate.Audio(new SynthesizedAudioChunk(
            sequence, AudioFormat.Pcm16(OutputSampleRate, OutputChannels), ReadOnlyMemory<byte>.Empty, IsFinal: true));
        double seconds = (double)totalBytes / (OutputSampleRate * OutputChannels * (OutputBitsPerSample / 8));
        yield return SpeechSynthesisStreamUpdate.Completed(new SpeechSynthesisResult(
            "openai://speech/stream",
            "audio/pcm",
            TimeSpan.FromSeconds(seconds),
            validated.Voice,
            new Dictionary<string, string>
            {
                ["adapter"] = AdapterKey,
                ["encoding"] = "pcm16le",
                ["sample-rate-hz"] = OutputSampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["language"] = validated.Language,
                ["input-tokens"] = inputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["output-tokens"] = outputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }));
    }

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

    private (string Text, string Voice, string Language, string? Instructions, RuntimeFailure? Failure)
        ValidateStreamingRequest(SpeechSynthesisRequest request)
    {
        string text = request.Text.Trim();
        if (text.Length == 0 || text.Length > options.MaximumSynthesisCharacters)
            return (text, request.Voice.VoiceId, request.Language, null,
                new RuntimeFailure("speech_synthesis_input_invalid", "The speech synthesis input is invalid.", false));
        string voice = request.Voice.VoiceId.Trim();
        if (voice.Length == 0 || voice.Length > 100
            || voice.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
            || request.Voice.SpeakingRate is < 0.5m or > 2.0m)
            return (text, voice, request.Language, null,
                new RuntimeFailure("speech_synthesis_configuration_invalid", "The speech voice configuration is invalid.", false));
        string? language = NormalizeLanguage(request.Language);
        if (language is null)
            return (text, voice, request.Language, null,
                new RuntimeFailure("speech_synthesis_language_invalid", "The speech language configuration is invalid.", false));
        string? instructions = options.SynthesisModel.StartsWith("gpt-4o-mini-tts", StringComparison.Ordinal)
            ? $"Speak in the configured {language} language with a {NormalizeStyle(request.Voice.Style)} delivery."
            : null;
        return (text, voice, language, instructions, null);
    }

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
