namespace PurpleGlass.Modules.CallManagement.Domain;

public sealed record RecordingMetadata
{
    public RecordingMetadata(
        string storageProvider,
        string objectReference,
        string contentType,
        long? durationMilliseconds,
        DateTimeOffset recordedAtUtc,
        DateTimeOffset retentionEligibleAtUtc,
        string? checksum,
        long? sizeBytes)
    {
        if (Path.IsPathRooted(objectReference))
        {
            throw new ArgumentException("Recording reference must be opaque and cannot be an absolute path.", nameof(objectReference));
        }

        if (durationMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        }

        if (sizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        }

        StorageProvider = Require(storageProvider, nameof(storageProvider), 100);
        ObjectReference = Require(objectReference, nameof(objectReference), 500);
        ContentType = Require(contentType, nameof(contentType), 150);
        DurationMilliseconds = durationMilliseconds;
        RecordedAtUtc = recordedAtUtc;
        RetentionEligibleAtUtc = retentionEligibleAtUtc;
        Checksum = Optional(checksum, nameof(checksum), 200);
        SizeBytes = sizeBytes;
    }

    public string StorageProvider { get; }
    public string ObjectReference { get; }
    public string ContentType { get; }
    public long? DurationMilliseconds { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public DateTimeOffset RetentionEligibleAtUtc { get; }
    public string? Checksum { get; }
    public long? SizeBytes { get; }

    private static string Require(string value, string name, int maximum)
    {
        string normalized = value.Trim();
        if (normalized.Length is 0 || normalized.Length > maximum)
        {
            throw new ArgumentException($"Value must contain between 1 and {maximum} characters.", name);
        }

        return normalized;
    }

    private static string? Optional(string? value, string name, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? null : Require(value, name, maximum);
}
