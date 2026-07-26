using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PurpleGlass.Modules.Conversation.Application;

namespace PurpleGlass.Adapters.Speech.OpenAI;

public sealed class OpenAiSpeechRecognizer : ISpeechRecognizer
{
    private readonly HttpClient httpClient;
    private readonly OpenAiSpeechOptions options;

    public OpenAiSpeechRecognizer(HttpClient httpClient, OpenAiSpeechOptions options)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.options = (options ?? throw new ArgumentNullException(nameof(options))).Validate();
    }

    public string AdapterKey => "openai";

    public async Task<SpeechRecognitionResult> RecognizeAsync(
        SpeechRecognitionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;

        RuntimeFailure? validationFailure = ValidateAudio(request.AudioInput);
        if (validationFailure is not null)
        {
            return Failure(request.Language, startedAtUtc, validationFailure);
        }

        string? language = NormalizeLanguage(request.Language);
        if (language is null)
        {
            return Failure(request.Language, startedAtUtc,
                new RuntimeFailure("speech_recognition_language_invalid", "The speech language configuration is invalid.", false));
        }

        byte[] wave = CreateWave(request.AudioInput!);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, options.TranscriptionsEndpoint);
        OpenAiSpeechHttp.AddAuthentication(httpRequest, options.ApiKey);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var form = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(wave);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audioContent, "file", "utterance.wav");
        form.Add(new StringContent(options.TranscriptionModel, Encoding.UTF8), "model");
        form.Add(new StringContent(language, Encoding.UTF8), "language");
        form.Add(new StringContent("json", Encoding.UTF8), "response_format");
        httpRequest.Content = form;

        try
        {
            using HttpResponseMessage response = await httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Failure(request.Language, startedAtUtc,
                    OpenAiSpeechHttp.MapRecognitionFailure(response.StatusCode));
            }

            byte[] responseBody = await OpenAiSpeechHttp.ReadBoundedAsync(
                response.Content,
                options.MaximumTranscriptionResponseBytes,
                cancellationToken);
            using JsonDocument document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("text", out JsonElement textElement)
                || textElement.ValueKind != JsonValueKind.String)
            {
                return Failure(request.Language, startedAtUtc,
                    new RuntimeFailure("speech_recognition_response_invalid", "Speech recognition returned an invalid response.", false));
            }

            string text = (textElement.GetString() ?? string.Empty).Trim();
            if (text.Length == 0 || text.Length > options.MaximumTranscriptCharacters)
            {
                return Failure(request.Language, startedAtUtc,
                    new RuntimeFailure("speech_recognition_response_invalid", "Speech recognition returned an invalid response.", false));
            }

            return new SpeechRecognitionResult(
                text,
                null,
                request.Language,
                startedAtUtc,
                DateTimeOffset.UtcNow,
                true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure(request.Language, startedAtUtc,
                new RuntimeFailure("speech_recognition_timeout", "Speech recognition timed out.", true));
        }
        catch (HttpRequestException)
        {
            return Failure(request.Language, startedAtUtc,
                new RuntimeFailure("speech_recognition_network_failed", "Speech recognition is temporarily unavailable.", true));
        }
        catch (IOException)
        {
            return Failure(request.Language, startedAtUtc,
                new RuntimeFailure("speech_recognition_network_failed", "Speech recognition is temporarily unavailable.", true));
        }
        catch (JsonException)
        {
            return Failure(request.Language, startedAtUtc,
                new RuntimeFailure("speech_recognition_response_invalid", "Speech recognition returned an invalid response.", false));
        }
        catch (OpenAiSpeechResponseTooLargeException)
        {
            return Failure(request.Language, startedAtUtc,
                new RuntimeFailure("speech_recognition_response_too_large", "Speech recognition exceeded the safe response size.", false));
        }
    }

    private RuntimeFailure? ValidateAudio(SpeechAudioInput? input)
    {
        if (input is null || input.Audio.IsEmpty || input.Audio.Length > options.MaximumInputAudioBytes
            || string.IsNullOrWhiteSpace(input.IdempotencyKey) || input.IdempotencyKey.Length > 200)
        {
            return new RuntimeFailure("speech_recognition_audio_invalid", "The speech audio input is invalid.", false);
        }

        AudioFormat format;
        try
        {
            format = input.Format.Validate();
        }
        catch (ArgumentException)
        {
            return new RuntimeFailure("speech_recognition_audio_invalid", "The speech audio input is invalid.", false);
        }

        int blockAlign = format.Channels * 2;
        return !format.Encoding.Equals("audio/pcm", StringComparison.OrdinalIgnoreCase)
            || format.BitsPerSample != 16 || input.Audio.Length % blockAlign != 0
            ? new RuntimeFailure("speech_recognition_audio_unsupported", "Speech recognition requires aligned PCM16 audio.", false)
            : null;
    }

    private static byte[] CreateWave(SpeechAudioInput input)
    {
        AudioFormat format = input.Format;
        int dataLength = input.Audio.Length;
        int blockAlign = checked(format.Channels * format.BitsPerSample / 8);
        int byteRate = checked(format.SampleRateHz * blockAlign);
        byte[] wave = new byte[checked(44 + dataLength)];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wave, 0);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4, 4), 36 + dataLength);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wave, 8);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22, 2), checked((short)format.Channels));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24, 4), format.SampleRateHz);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28, 4), byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(32, 2), checked((short)blockAlign));
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(34, 2), checked((short)format.BitsPerSample));
        Encoding.ASCII.GetBytes("data").CopyTo(wave, 36);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40, 4), dataLength);
        input.Audio.Span.CopyTo(wave.AsSpan(44));
        return wave;
    }

    private static string? NormalizeLanguage(string value)
    {
        string normalized = value.Trim();
        int separator = normalized.IndexOfAny(['-', '_']);
        if (separator > 0)
        {
            normalized = normalized[..separator];
        }

        return normalized.Length == 2 && normalized.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            ? normalized.ToLowerInvariant()
            : null;
    }

    private static SpeechRecognitionResult Failure(
        string language,
        DateTimeOffset startedAtUtc,
        RuntimeFailure failure) => new(
            string.Empty,
            null,
            language,
            startedAtUtc,
            DateTimeOffset.UtcNow,
            true,
            failure);
}
