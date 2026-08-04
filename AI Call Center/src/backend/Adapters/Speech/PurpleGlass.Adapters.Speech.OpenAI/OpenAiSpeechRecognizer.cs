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
        var form = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(wave);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audioContent, "file", "utterance.wav");
        form.Add(new StringContent(options.TranscriptionModel, Encoding.UTF8), "model");
        bool detectedLanguageStream = options.TranscriptionModel.Equals("gpt-transcribe", StringComparison.OrdinalIgnoreCase);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            detectedLanguageStream ? "text/event-stream" : "application/json"));
        if (detectedLanguageStream)
            form.Add(new StringContent("true", Encoding.UTF8), "stream");
        else
        {
            form.Add(new StringContent(language, Encoding.UTF8), "language");
            form.Add(new StringContent("json", Encoding.UTF8), "response_format");
        }
        httpRequest.Content = form;

        string httpStatusCategory = "not_received";
        string contentTypeCategory = "not_received";
        string stage = "request_started";

        try
        {
            using HttpResponseMessage response = await httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            stage = "response_headers_received";
            httpStatusCategory = StatusCategory(response.StatusCode);
            contentTypeCategory = ContentTypeCategory(response.Content.Headers.ContentType?.MediaType);
            if (!response.IsSuccessStatusCode)
            {
                RuntimeFailure failure = OpenAiSpeechHttp.MapRecognitionFailure(response.StatusCode);
                return Failure(request.Language, startedAtUtc,
                    failure,
                    Diagnostic(
                        stage,
                        failure.Retryable ? "provider_transient" : "provider_rejected",
                        httpStatusCategory,
                        contentTypeCategory,
                        "provider_error_status",
                        transcriptPresent: false,
                        languageMetadataPresent: false,
                        "absent"));
            }

            string expectedContentType = detectedLanguageStream ? "event_stream" : "json";
            if (!contentTypeCategory.Equals(expectedContentType, StringComparison.Ordinal))
            {
                return InvalidResponse(request.Language, startedAtUtc,
                    stage, httpStatusCategory, contentTypeCategory, "unexpected_content_type");
            }

            stage = "response_parsing";
            ParsedTranscription parsed = detectedLanguageStream
                ? await ReadDetectedLanguageStreamAsync(response.Content, cancellationToken)
                : await ReadJsonAsync(response.Content, cancellationToken);
            if (parsed.Text.Length == 0)
            {
                return new SpeechRecognitionResult(
                    string.Empty,
                    null,
                    request.Language,
                    startedAtUtc,
                    DateTimeOffset.UtcNow,
                    true,
                    DetectedLanguages: parsed.Languages,
                    ResponseDiagnostic: Diagnostic(
                        "response_completed",
                        "empty_result",
                        httpStatusCategory,
                        contentTypeCategory,
                        "valid_empty_transcript",
                        transcriptPresent: false,
                        parsed.LanguageMetadataPresent,
                        parsed.LanguageMetadataCategory));
            }
            if (parsed.Text.Length > options.MaximumTranscriptCharacters)
            {
                return InvalidResponse(request.Language, startedAtUtc,
                    "response_validation", httpStatusCategory, contentTypeCategory,
                    "transcript_too_large", transcriptPresent: true,
                    parsed.LanguageMetadataPresent, parsed.LanguageMetadataCategory);
            }

            return new SpeechRecognitionResult(
                parsed.Text,
                null,
                request.Language,
                startedAtUtc,
                DateTimeOffset.UtcNow,
                true,
                DetectedLanguages: parsed.Languages,
                ResponseDiagnostic: Diagnostic(
                    "response_completed",
                    "success",
                    httpStatusCategory,
                    contentTypeCategory,
                    "valid",
                    transcriptPresent: true,
                    parsed.LanguageMetadataPresent,
                    parsed.LanguageMetadataCategory));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure(request.Language, startedAtUtc,
                new RuntimeFailure("speech_recognition_timeout", "Speech recognition timed out.", true),
                Diagnostic(stage, "provider_transient", httpStatusCategory, contentTypeCategory,
                    "timeout", false, false, "absent"));
        }
        catch (HttpRequestException)
        {
            return Failure(request.Language, startedAtUtc,
                new RuntimeFailure("speech_recognition_network_failed", "Speech recognition is temporarily unavailable.", true),
                Diagnostic(stage, "provider_transient", httpStatusCategory, contentTypeCategory,
                    "network_failure", false, false, "absent"));
        }
        catch (IOException)
        {
            return Failure(request.Language, startedAtUtc,
                new RuntimeFailure("speech_recognition_network_failed", "Speech recognition is temporarily unavailable.", true),
                Diagnostic(stage, "provider_transient", httpStatusCategory, contentTypeCategory,
                    "network_failure", false, false, "absent"));
        }
        catch (OpenAiTranscriptionResponseException exception)
        {
            RuntimeFailure failure = exception.ProviderRejected
                ? new RuntimeFailure("speech_recognition_provider_rejected", "Speech recognition was rejected by the provider.", false)
                : new RuntimeFailure("speech_recognition_response_invalid", "Speech recognition returned an invalid response.", false);
            return Failure(request.Language, startedAtUtc, failure,
                Diagnostic(stage,
                    exception.ProviderRejected ? "provider_rejected" : "invalid_response_schema",
                    httpStatusCategory, contentTypeCategory, exception.ShapeCategory,
                    exception.TranscriptPresent, exception.LanguageMetadataPresent,
                    exception.LanguageMetadataCategory));
        }
        catch (JsonException)
        {
            return InvalidResponse(request.Language, startedAtUtc,
                stage, httpStatusCategory, contentTypeCategory, "malformed_json");
        }
        catch (OpenAiSpeechResponseTooLargeException)
        {
            return Failure(request.Language, startedAtUtc,
                new RuntimeFailure("speech_recognition_response_too_large", "Speech recognition exceeded the safe response size.", false),
                Diagnostic(stage, "invalid_response_schema", httpStatusCategory, contentTypeCategory,
                    "response_too_large", false, false, "absent"));
        }
    }

    private async Task<ParsedTranscription> ReadJsonAsync(
        HttpContent content, CancellationToken cancellationToken)
    {
        byte[] responseBody = await OpenAiSpeechHttp.ReadBoundedAsync(
            content, options.MaximumTranscriptionResponseBytes, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(responseBody);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw OpenAiTranscriptionResponseException.Invalid("unexpected_root");
        if (root.TryGetProperty("error", out _))
            throw OpenAiTranscriptionResponseException.Rejected("provider_error_envelope");
        if (!root.TryGetProperty("text", out JsonElement textElement))
            throw OpenAiTranscriptionResponseException.Invalid("missing_text");
        if (textElement.ValueKind != JsonValueKind.String)
            throw OpenAiTranscriptionResponseException.Invalid("wrong_text_type");
        LanguageMetadata languages = ReadLanguages(root);
        return new ParsedTranscription(
            (textElement.GetString() ?? string.Empty).Trim(),
            languages.Codes,
            languages.Present,
            languages.Category);
    }

    private async Task<ParsedTranscription> ReadDetectedLanguageStreamAsync(
        HttpContent content, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: false);
        int consumedBytes = 0;
        string? finalText = null;
        LanguageMetadata languages = LanguageMetadata.Absent;
        bool finalEventSeen = false;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            consumedBytes = checked(consumedBytes + Encoding.UTF8.GetByteCount(line) + 1);
            if (consumedBytes > options.MaximumTranscriptionResponseBytes)
                throw new OpenAiSpeechResponseTooLargeException();
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            string json = line[5..].Trim();
            if (json.Length == 0 || json == "[DONE]") continue;
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw OpenAiTranscriptionResponseException.Invalid("unexpected_stream_event");
            if (root.TryGetProperty("error", out _))
                throw OpenAiTranscriptionResponseException.Rejected("provider_error_envelope");
            if (!root.TryGetProperty("type", out JsonElement type)
                || !string.Equals(type.GetString(), "transcript.text.done", StringComparison.Ordinal)) continue;
            if (finalEventSeen)
                throw OpenAiTranscriptionResponseException.Invalid("duplicate_final_event");
            finalEventSeen = true;
            if (!root.TryGetProperty("text", out JsonElement text))
                throw OpenAiTranscriptionResponseException.Invalid("missing_text");
            if (text.ValueKind != JsonValueKind.String)
                throw OpenAiTranscriptionResponseException.Invalid("wrong_text_type");
            finalText = text.GetString();
            languages = ReadLanguages(root);
        }
        if (!finalEventSeen)
            throw OpenAiTranscriptionResponseException.Invalid("missing_final_event");
        return new ParsedTranscription(
            (finalText ?? string.Empty).Trim(),
            languages.Codes,
            languages.Present,
            languages.Category);
    }

    private static LanguageMetadata ReadLanguages(JsonElement root)
    {
        var languages = new List<string>(2);
        bool present = false;
        bool invalid = false;
        if (root.TryGetProperty("languages", out JsonElement array) && array.ValueKind == JsonValueKind.Array)
        {
            present = true;
            foreach (JsonElement item in array.EnumerateArray())
            {
                string? code = item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : item.ValueKind == JsonValueKind.Object && item.TryGetProperty("code", out JsonElement codeElement)
                        ? codeElement.GetString() : null;
                if (IsBoundedLanguageCode(code) && !languages.Contains(code!, StringComparer.OrdinalIgnoreCase))
                    languages.Add(code!.ToLowerInvariant());
                else if (!IsBoundedLanguageCode(code)) invalid = true;
            }
        }
        else if (root.TryGetProperty("languages", out _))
        {
            present = true;
            invalid = true;
        }
        else if (root.TryGetProperty("language", out JsonElement language))
        {
            present = true;
            if (language.ValueKind == JsonValueKind.String && IsBoundedLanguageCode(language.GetString()))
                languages.Add(language.GetString()!.ToLowerInvariant());
            else invalid = true;
        }
        string category = !present ? "absent"
            : invalid && languages.Count > 0 ? "partially_invalid"
            : invalid ? "invalid"
            : languages.Count > 1 ? "multiple"
            : "present";
        return new LanguageMetadata(languages, present, category);
    }

    private static bool IsBoundedLanguageCode(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 35
        && value.All(character => char.IsLetter(character) || character is '-' or '_');

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
        RuntimeFailure failure,
        SpeechRecognitionResponseDiagnostic? diagnostic = null) => new(
            string.Empty,
            null,
            language,
            startedAtUtc,
            DateTimeOffset.UtcNow,
            true,
            failure,
            ResponseDiagnostic: diagnostic);

    private static SpeechRecognitionResult InvalidResponse(
        string language,
        DateTimeOffset startedAtUtc,
        string stage,
        string httpStatusCategory,
        string contentTypeCategory,
        string responseShapeCategory,
        bool transcriptPresent = false,
        bool languageMetadataPresent = false,
        string languageMetadataCategory = "absent") => Failure(
            language,
            startedAtUtc,
            new RuntimeFailure("speech_recognition_response_invalid", "Speech recognition returned an invalid response.", false),
            Diagnostic(stage, "invalid_response_schema", httpStatusCategory, contentTypeCategory,
                responseShapeCategory, transcriptPresent, languageMetadataPresent, languageMetadataCategory));

    private static SpeechRecognitionResponseDiagnostic Diagnostic(
        string stage,
        string resultCategory,
        string httpStatusCategory,
        string contentTypeCategory,
        string responseShapeCategory,
        bool transcriptPresent,
        bool languageMetadataPresent,
        string languageMetadataCategory) => new(
            "audio_transcription",
            stage,
            resultCategory,
            httpStatusCategory,
            contentTypeCategory,
            responseShapeCategory,
            transcriptPresent,
            languageMetadataPresent,
            languageMetadataCategory);

    private static string StatusCategory(System.Net.HttpStatusCode statusCode)
    {
        int status = (int)statusCode;
        return status switch
        {
            >= 200 and < 300 => "success",
            408 => "timeout",
            429 => "rate_limited",
            >= 400 and < 500 => "client_error",
            >= 500 => "server_error",
            _ => "other",
        };
    }

    private static string ContentTypeCategory(string? mediaType) => mediaType?.ToLowerInvariant() switch
    {
        "application/json" => "json",
        "text/event-stream" => "event_stream",
        null or "" => "missing",
        _ => "unexpected",
    };

    private sealed record ParsedTranscription(
        string Text,
        IReadOnlyList<string> Languages,
        bool LanguageMetadataPresent,
        string LanguageMetadataCategory);

    private sealed record LanguageMetadata(
        IReadOnlyList<string> Codes,
        bool Present,
        string Category)
    {
        public static LanguageMetadata Absent { get; } = new([], false, "absent");
    }

    private sealed class OpenAiTranscriptionResponseException : Exception
    {
        private OpenAiTranscriptionResponseException(
            string shapeCategory,
            bool providerRejected,
            bool transcriptPresent = false,
            bool languageMetadataPresent = false,
            string languageMetadataCategory = "absent")
            : base("The bounded transcription response shape was invalid.")
        {
            ShapeCategory = shapeCategory;
            ProviderRejected = providerRejected;
            TranscriptPresent = transcriptPresent;
            LanguageMetadataPresent = languageMetadataPresent;
            LanguageMetadataCategory = languageMetadataCategory;
        }

        public string ShapeCategory { get; }
        public bool ProviderRejected { get; }
        public bool TranscriptPresent { get; }
        public bool LanguageMetadataPresent { get; }
        public string LanguageMetadataCategory { get; }

        public static OpenAiTranscriptionResponseException Invalid(string shapeCategory) => new(shapeCategory, false);
        public static OpenAiTranscriptionResponseException Rejected(string shapeCategory) => new(shapeCategory, true);
    }
}
