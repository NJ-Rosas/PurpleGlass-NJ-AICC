using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PurpleGlass.Observability;

public static class PurpleGlassTelemetry
{
    public const string WebBffSourceName = "PurpleGlass.WebBff";
    public const string CallOrchestratorSourceName = "PurpleGlass.CallOrchestrator";
    public const string EventingSourceName = "PurpleGlass.Eventing";
    public const string MessagingSourceName = "PurpleGlass.Messaging";
    public const string SecuritySourceName = "PurpleGlass.Security";
    public const string IntegrationsSourceName = "PurpleGlass.Integrations";

    public static readonly ActivitySource WebBff = new(WebBffSourceName);
    public static readonly ActivitySource Calls = new(CallOrchestratorSourceName);
    public static readonly ActivitySource Eventing = new(EventingSourceName);
    public static readonly ActivitySource Messaging = new(MessagingSourceName);
    public static readonly ActivitySource Security = new(SecuritySourceName);
    public static readonly ActivitySource Integrations = new(IntegrationsSourceName);

    public static readonly Meter Meter = new("PurpleGlass", "1.0.0");
    public static readonly Counter<long> CallsStarted = Meter.CreateCounter<long>("purpleglass.calls.started");
    public static readonly Counter<long> CallsCompleted = Meter.CreateCounter<long>("purpleglass.calls.completed");
    public static readonly Counter<long> CallsFailed = Meter.CreateCounter<long>("purpleglass.calls.failed");
    public static readonly Counter<long> CallsEscalated = Meter.CreateCounter<long>("purpleglass.calls.escalated");
    public static readonly Histogram<double> CallsDuration = Meter.CreateHistogram<double>("purpleglass.calls.duration", "ms");
    public static readonly Counter<long> ConversationsStarted = Meter.CreateCounter<long>("purpleglass.conversations.started");
    public static readonly Counter<long> ConversationsCompleted = Meter.CreateCounter<long>("purpleglass.conversations.completed");
    public static readonly Counter<long> AiRequests = Meter.CreateCounter<long>("purpleglass.ai.requests");
    public static readonly Counter<long> AiFailures = Meter.CreateCounter<long>("purpleglass.ai.failures");
    public static readonly Histogram<double> AiDuration = Meter.CreateHistogram<double>("purpleglass.ai.duration", "ms");
    public static readonly Counter<long> SpeechRecognitionRequests = Meter.CreateCounter<long>("purpleglass.speech.recognition.requests");
    public static readonly Counter<long> SpeechRecognitionFailures = Meter.CreateCounter<long>("purpleglass.speech.recognition.failures");
    public static readonly Histogram<double> SpeechRecognitionDuration = Meter.CreateHistogram<double>("purpleglass.speech.recognition.duration", "ms");
    public static readonly Counter<long> SpeechSynthesisRequests = Meter.CreateCounter<long>("purpleglass.speech.synthesis.requests");
    public static readonly Counter<long> SpeechSynthesisFailures = Meter.CreateCounter<long>("purpleglass.speech.synthesis.failures");
    public static readonly Histogram<double> SpeechSynthesisDuration = Meter.CreateHistogram<double>("purpleglass.speech.synthesis.duration", "ms");
    public static readonly Counter<long> OutboxLeased = Meter.CreateCounter<long>("purpleglass.outbox.leased");
    public static readonly Counter<long> OutboxLeaseRecovered = Meter.CreateCounter<long>("purpleglass.outbox.lease_recovered");
    public static readonly Histogram<double> OutboxAge = Meter.CreateHistogram<double>("purpleglass.outbox.age", "s");
    public static readonly Histogram<double> OutboxPublishDuration = Meter.CreateHistogram<double>("purpleglass.outbox.publish.duration", "ms");
    public static readonly Counter<long> OutboxPublishFailures = Meter.CreateCounter<long>("purpleglass.outbox.publish.failures");
    public static readonly Counter<long> OutboxRetries = Meter.CreateCounter<long>("purpleglass.outbox.retries");
    public static readonly Counter<long> OutboxDeadLetters = Meter.CreateCounter<long>("purpleglass.outbox.deadletters");
    public static readonly Counter<long> DeadLettersRequeued = Meter.CreateCounter<long>("purpleglass.deadletter.requeued");
    public static readonly Counter<long> DeadLetterRequeueFailures = Meter.CreateCounter<long>("purpleglass.deadletter.requeue.failed");
    public static readonly Counter<long> RecoveredMessagesCompleted = Meter.CreateCounter<long>("purpleglass.deadletter.recovered.completed");
    public static readonly Counter<long> RecoveredMessagesFailed = Meter.CreateCounter<long>("purpleglass.deadletter.recovered.failed");
    public static readonly Counter<long> InboxProcessed = Meter.CreateCounter<long>("purpleglass.inbox.processed");
    public static readonly Counter<long> InboxDuplicates = Meter.CreateCounter<long>("purpleglass.inbox.duplicates");
    public static readonly Counter<long> InboxFailures = Meter.CreateCounter<long>("purpleglass.inbox.failures");
    public static readonly Counter<long> MqttPublished = Meter.CreateCounter<long>("purpleglass.mqtt.publish");
    public static readonly Counter<long> MqttPublishFailures = Meter.CreateCounter<long>("purpleglass.mqtt.publish.failures");
    public static readonly Counter<long> MqttReceived = Meter.CreateCounter<long>("purpleglass.mqtt.received");
    public static readonly Counter<long> MqttReconnects = Meter.CreateCounter<long>("purpleglass.mqtt.reconnects");
    public static readonly Counter<long> TelephonyInboundCalls = Meter.CreateCounter<long>("purpleglass.telephony.calls.inbound");
    public static readonly Counter<long> TelephonyOutboundCalls = Meter.CreateCounter<long>("purpleglass.telephony.calls.outbound");
    public static readonly Counter<long> TelephonyCallsConnected = Meter.CreateCounter<long>("purpleglass.telephony.calls.connected");
    public static readonly Counter<long> TelephonyCallsFailed = Meter.CreateCounter<long>("purpleglass.telephony.calls.failed");
    public static readonly Counter<long> TelephonyWebhooksReceived = Meter.CreateCounter<long>("purpleglass.telephony.webhook.received");
    public static readonly Counter<long> TelephonyWebhooksInvalid = Meter.CreateCounter<long>("purpleglass.telephony.webhook.invalid");
    public static readonly Counter<long> TelephonyProviderErrors = Meter.CreateCounter<long>("purpleglass.telephony.provider.errors");
    public static readonly Histogram<double> TelephonyCallDuration = Meter.CreateHistogram<double>("purpleglass.telephony.call.duration", "s");
    public static readonly Counter<long> SecurityAuthSuccess = Meter.CreateCounter<long>("purpleglass.security.auth.success");
    public static readonly Counter<long> SecurityAuthFailure = Meter.CreateCounter<long>("purpleglass.security.auth.failure");
    public static readonly Counter<long> SecurityAuthorizationDenied = Meter.CreateCounter<long>("purpleglass.security.authorization.denied");
    public static readonly Counter<long> SecurityCsrfFailure = Meter.CreateCounter<long>("purpleglass.security.csrf.failure");
    public static readonly Counter<long> SecurityRateLimitRejected = Meter.CreateCounter<long>("purpleglass.security.rate_limit.rejected");
    public static readonly UpDownCounter<long> ActiveSseConnections = Meter.CreateUpDownCounter<long>("purpleglass.sse.connections.active");
    public static readonly Counter<long> SseDeliveryFailures = Meter.CreateCounter<long>("purpleglass.sse.delivery.failures");
}
