using PurpleGlass.Modules.CallManagement.Domain;

namespace PurpleGlass.UnitTests;

public sealed class CallSessionTests
{
    [Fact]
    public void InboundCallCanCompleteAndAttachRecordingReference()
    {
        DateTimeOffset receivedAt = DateTimeOffset.UtcNow;
        CallSession call = CreateInbound(receivedAt);

        call.Answer(receivedAt.AddSeconds(1));
        call.StartConversation();
        call.Complete("Caller request captured", receivedAt.AddMinutes(2));
        call.AttachRecording("recordings/demo-call.wav", receivedAt.AddDays(30));

        Assert.Equal(CallState.Completed, call.State);
        Assert.Equal("Caller request captured", call.Outcome);
        Assert.Equal("recordings/demo-call.wav", call.RecordingReference);
        Assert.Equal(receivedAt.AddDays(30), call.RecordingRetentionEligibleAtUtc);
        Assert.Equal(5, call.Version);
    }

    [Fact]
    public void OutboundCallMustRingBeforeItCanBeAnswered()
    {
        DateTimeOffset requestedAt = DateTimeOffset.UtcNow;
        CallSession call = CallSession.RequestOutbound(
            CallSessionId.New(),
            new TenantId(Guid.NewGuid()),
            new LocationId(Guid.NewGuid()),
            "provider-outbound-1",
            "+15550000001",
            "+15550000002",
            Guid.NewGuid(),
            requestedAt);

        _ = Assert.Throws<InvalidOperationException>(() => call.Answer(requestedAt.AddSeconds(1)));

        call.MarkRinging();
        call.Answer(requestedAt.AddSeconds(2));

        Assert.Equal(CallState.Answered, call.State);
    }

    [Fact]
    public void RecordingCannotBeAttachedToActiveCall()
    {
        CallSession call = CreateInbound(DateTimeOffset.UtcNow);

        _ = Assert.Throws<InvalidOperationException>(() =>
            call.AttachRecording("recordings/active.wav", DateTimeOffset.UtcNow.AddDays(30)));
    }

    [Fact]
    public void CompletedCallCannotTransitionAgain()
    {
        DateTimeOffset receivedAt = DateTimeOffset.UtcNow;
        CallSession call = CreateInbound(receivedAt);
        call.Answer(receivedAt.AddSeconds(1));
        call.Complete("Answered", receivedAt.AddMinutes(1));

        _ = Assert.Throws<InvalidOperationException>(() => call.Fail("late failure", receivedAt.AddMinutes(2)));
    }

    private static CallSession CreateInbound(DateTimeOffset receivedAt) => CallSession.ReceiveInbound(
        CallSessionId.New(),
        new TenantId(Guid.NewGuid()),
        new LocationId(Guid.NewGuid()),
        "provider-inbound-1",
        "+15550000002",
        "+15550000001",
        Guid.NewGuid(),
        receivedAt);
}
