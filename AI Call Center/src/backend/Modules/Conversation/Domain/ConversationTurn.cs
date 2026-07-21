namespace PurpleGlass.Modules.Conversation.Domain;

public sealed class ConversationTurn
{
    private ConversationTurn()
    {
    }

    internal ConversationTurn(
        ConversationTurnId id,
        ConversationId conversationId,
        SpeakerRole speaker,
        int sequenceNumber,
        string text,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset? endedAtUtc,
        decimal? recognitionConfidence,
        DateTimeOffset createdAtUtc,
        bool safetyFlagged,
        bool escalationFlagged)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("Turn identifier is required.", nameof(id));
        }

        if (sequenceNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequenceNumber), "Sequence number must be positive.");
        }

        if (startedAtUtc.HasValue && endedAtUtc < startedAtUtc)
        {
            throw new ArgumentException("Turn end time cannot precede its start time.", nameof(endedAtUtc));
        }

        if (recognitionConfidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(recognitionConfidence), "Recognition confidence must be between 0 and 1.");
        }

        Id = id;
        ConversationId = conversationId;
        Speaker = speaker;
        SequenceNumber = sequenceNumber;
        Text = RequireText(text);
        StartedAtUtc = startedAtUtc;
        EndedAtUtc = endedAtUtc;
        RecognitionConfidence = recognitionConfidence;
        CreatedAtUtc = createdAtUtc;
        SafetyFlagged = safetyFlagged;
        EscalationFlagged = escalationFlagged;
    }

    public ConversationTurnId Id { get; private set; }

    public ConversationId ConversationId { get; private set; }

    public SpeakerRole Speaker { get; private set; }

    public int SequenceNumber { get; private set; }

    public string Text { get; private set; } = string.Empty;

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? EndedAtUtc { get; private set; }

    public decimal? RecognitionConfidence { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public bool SafetyFlagged { get; private set; }

    public bool EscalationFlagged { get; private set; }

    private static string RequireText(string text)
    {
        string normalized = text.Trim();
        if (normalized.Length is 0 or > 8_000)
        {
            throw new ArgumentException("Turn text must contain between 1 and 8000 characters.", nameof(text));
        }

        return normalized;
    }
}
