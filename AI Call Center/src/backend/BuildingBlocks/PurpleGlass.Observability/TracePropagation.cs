using System.Diagnostics;

namespace PurpleGlass.Observability;

public readonly record struct TraceEnvelope(string? TraceId, string? TraceParent, string? TraceState)
{
    public static TraceEnvelope CaptureCurrent()
    {
        Activity? activity = Activity.Current;
        return activity is null
            ? default
            : new(activity.TraceId.ToHexString(), activity.Id, activity.TraceStateString);
    }

    public bool TryExtract(out ActivityContext context) =>
        ActivityContext.TryParse(TraceParent, TraceState, isRemote: true, out context);
}

public static class CorrelationIds
{
    public static Guid PreserveOrCreate(Guid? candidate) => candidate is { } value && value != Guid.Empty ? value : Guid.NewGuid();
}

public static class TelemetrySanitizer
{
    public const string UnknownErrorCode = "operation_failed";

    public static string ErrorCode(Exception exception) => exception switch
    {
        TimeoutException => "timeout",
        OperationCanceledException => "cancelled",
        _ => UnknownErrorCode
    };
}
