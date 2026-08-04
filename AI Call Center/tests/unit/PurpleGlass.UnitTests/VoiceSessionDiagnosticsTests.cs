using Microsoft.Extensions.Logging;
using PurpleGlass.Modules.Conversation.Application;
using PurpleGlass.WebBff;

namespace PurpleGlass.UnitTests;

public sealed class VoiceSessionDiagnosticsTests
{
    [Fact]
    public void SpeechRecognitionDiagnosticLogContainsOnlyBoundedOperationalFields()
    {
        var logger = new CapturingLogger<BffVoiceSessionDiagnostics>();
        var diagnostics = new BffVoiceSessionDiagnostics(logger);

        diagnostics.RecordSpeechRecognition(new VoiceSpeechRecognitionDiagnostic(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "trace-safe", "turn-safe",
            "openai", "audio_transcription", "response_parsing", "invalid_response_schema",
            "success", "event_stream", "private-patient-provider-payload", false, false, "absent",
            CancellationRequested: false, SessionClosing: false, CallerDisconnected: false,
            RetryAttempted: false, RecoveryDecision: "end_turn"));

        CapturedLog entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("ResultCategory=invalid_response_schema", entry.Message, StringComparison.Ordinal);
        Assert.Contains("ResponseShapeCategory=other", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-patient-provider-payload", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Destination", entry.PropertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("PhoneNumber", entry.PropertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Transcript", entry.PropertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("RequestBody", entry.PropertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Audio", entry.PropertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("IdempotencyKey", entry.PropertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("ApiKey", entry.PropertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("ExceptionMessage", entry.PropertyNames, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<CapturedLog> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string[] propertyNames = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.Select(value => value.Key).ToArray()
                : [];
            Entries.Add(new CapturedLog(logLevel, formatter(state, exception), propertyNames));
        }
    }

    private sealed record CapturedLog(LogLevel Level, string Message, IReadOnlyList<string> PropertyNames);
}
