using PurpleGlass.Adapters.Telephony.Fake;
using PurpleGlass.Adapters.Telephony.Twilio;
using PurpleGlass.Modules.CallManagement.Application;
using PurpleGlass.Modules.CallManagement.Domain;

namespace PurpleGlass.UnitTests;

public sealed class TelephonyBoundaryTests
{
    [Theory]
    [InlineData("+1 (787) 555-1234", "+17875551234")]
    [InlineData(" +442071838750 ", "+442071838750")]
    public void PhoneNumbersNormalizeDeterministically(string input, string expected) =>
        Assert.Equal(expected, PhoneNumber.Normalize(input));

    [Theory]
    [InlineData("7875551234")]
    [InlineData("+012345678")]
    [InlineData("+123")]
    public void InvalidPhoneNumbersAreRejected(string input) =>
        Assert.Throws<ArgumentException>(() => PhoneNumber.Normalize(input));

    [Fact]
    public async Task FakeProviderIsDeterministicAndSwappable()
    {
        var provider = new FakeTelephonyProvider();
        Guid operationId = Guid.NewGuid();
        var request = new OutboundCallTransport(operationId, Guid.NewGuid(), "+17875550100", "+17875550101",
            new Uri("https://example.test/answer"), new Uri("https://example.test/status"));
        OutboundCallResult first = await provider.StartOutboundCallAsync(request, default);
        OutboundCallResult replay = await provider.StartOutboundCallAsync(request, default);
        Assert.True(first.Succeeded);
        Assert.Equal(first.ProviderCallId, replay.ProviderCallId);
    }

    [Fact]
    public async Task DisabledProviderFailsSafelyWithoutCredentials()
    {
        var provider = new DisabledTelephonyProvider();
        OutboundCallResult result = await provider.StartOutboundCallAsync(new OutboundCallTransport(
            Guid.NewGuid(), Guid.NewGuid(), "+17875550100", "+17875550101",
            new Uri("https://example.test/answer"), new Uri("https://example.test/status")), default);
        Assert.False(result.Succeeded);
        Assert.Equal("provider_disabled", result.SafeErrorCode);
        Assert.Equal("disabled", provider.Status.State);
    }

    [Fact]
    public void TwilioVerifierAcceptsOfficialSignatureAndRejectsInvalidSignature()
    {
        const string token = "synthetic-test-auth-token";
        const string url = "https://example.test/telephony/twilio/inbound";
        var parameters = new Dictionary<string, string>
        {
            ["CallSid"] = "CA11111111111111111111111111111111",
            ["From"] = "+17875550100",
            ["To"] = "+17875550101",
        };
        const string signature = "FjvnB1z6Nu8TJ/Y2HlilS78kQus=";
        var verifier = new TwilioWebhookVerifier(new TwilioTelephonyOptions
        {
            AccountSid = "AC11111111111111111111111111111111",
            AuthToken = token,
            PublicBaseUrl = "https://example.test",
        });
        Assert.True(verifier.Verify(url, parameters, signature));
        Assert.False(verifier.Verify(url, parameters, "invalid"));
    }
}
