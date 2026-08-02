using PurpleGlass.SharedKernel;

namespace PurpleGlass.Modules.Conversation.Domain;

public sealed class ConversationLanguageChange
{
    private ConversationLanguageChange() { }

    public ConversationLanguageChange(Guid id, ConversationId conversationId, long sequence,
        string previousLanguageCode, string languageCode, string reason,
        DateTimeOffset changedAtUtc, decimal? detectionConfidence)
    {
        if (id == Guid.Empty) throw new ArgumentException("Identifier is required.", nameof(id));
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        Id = id;
        ConversationId = conversationId;
        Sequence = sequence;
        PreviousLanguageCode = SupportedCallLanguages.Require(previousLanguageCode).Code;
        LanguageCode = SupportedCallLanguages.Require(languageCode).Code;
        Reason = string.IsNullOrWhiteSpace(reason) || reason.Length > 40
            ? throw new ArgumentException("A bounded language reason is required.", nameof(reason))
            : reason.Trim();
        ChangedAtUtc = changedAtUtc;
        DetectionConfidence = detectionConfidence is < 0 or > 1
            ? throw new ArgumentOutOfRangeException(nameof(detectionConfidence)) : detectionConfidence;
    }

    public Guid Id { get; private set; }
    public ConversationId ConversationId { get; private set; }
    public long Sequence { get; private set; }
    public string PreviousLanguageCode { get; private set; } = string.Empty;
    public string LanguageCode { get; private set; } = string.Empty;
    public string Reason { get; private set; } = string.Empty;
    public DateTimeOffset ChangedAtUtc { get; private set; }
    public decimal? DetectionConfidence { get; private set; }
}
