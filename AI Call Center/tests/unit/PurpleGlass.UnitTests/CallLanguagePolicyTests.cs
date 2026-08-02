using PurpleGlass.Modules.Conversation.Application;
using PurpleGlass.SharedKernel;

namespace PurpleGlass.UnitTests;

public sealed class CallLanguagePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 3, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("english", "en-US")]
    [InlineData("es_us", "es-US")]
    [InlineData("es-PR", "es-PR")]
    public void SupportedRegistryNormalizesBoundedAliases(string input, string expected)
    {
        Assert.True(SupportedCallLanguages.TryNormalize(input, out SupportedCallLanguage language));
        Assert.Equal(expected, language.Code);
    }

    [Fact]
    public void UnsupportedRegistryValueIsRejected()
    {
        Assert.False(SupportedCallLanguages.TryNormalize("arbitrary-language", out _));
        _ = Assert.Throws<ArgumentException>(() => SupportedCallLanguages.Require("arbitrary-language"));
    }

    [Theory]
    [InlineData("Speak Spanish.", "en-US", "es-US")]
    [InlineData("Háblame en inglés.", "es-PR", "en-US")]
    [InlineData("Cambia a español de Puerto Rico.", "en-US", "es-PR")]
    public void ExplicitSupportedRequestSwitchesImmediately(string transcript, string start, string expected)
    {
        var state = new CallLanguageState(start, CallLanguageReasons.LocationDefault, Now);

        CallLanguageDecision decision = state.Evaluate(transcript, Recognition(), Now.AddSeconds(1));

        Assert.True(decision.IsLanguageRequest);
        Assert.True(decision.Accepted);
        Assert.Equal(expected, decision.ActiveLanguageCode);
        Assert.Equal(CallLanguageReasons.CallerExplicitRequest, decision.Reason);
        Assert.Equal(1, decision.Version);
    }

    [Fact]
    public void RepeatedExplicitRequestDoesNotCreateAnotherChange()
    {
        var state = new CallLanguageState("en-US", CallLanguageReasons.LocationDefault, Now);

        CallLanguageDecision decision = state.Evaluate("Please speak English.", Recognition(), Now.AddSeconds(1));

        Assert.False(decision.Accepted);
        Assert.Equal("already_active", decision.Result);
        Assert.Equal(0, decision.Version);
    }

    [Fact]
    public void UnsupportedExplicitRequestRetainsActiveLanguage()
    {
        var state = new CallLanguageState("es-PR", CallLanguageReasons.CallOverride, Now);

        CallLanguageDecision decision = state.Evaluate("Por favor, habla francés.", Recognition(), Now.AddSeconds(1));

        Assert.True(decision.IsLanguageRequest);
        Assert.True(decision.Unsupported);
        Assert.False(decision.Accepted);
        Assert.Equal("es-PR", decision.ActiveLanguageCode);
    }

    [Fact]
    public void TwoMeaningfulAlternateTurnsSwitchAutomatically()
    {
        var state = new CallLanguageState("en-US", CallLanguageReasons.LocationDefault, Now);

        CallLanguageDecision first = state.Evaluate(
            "Necesito hacer una cita con el dentista mañana.", Recognition("es", 0.94m), Now.AddSeconds(1));
        CallLanguageDecision second = state.Evaluate(
            "También tengo dolor en una muela desde ayer.", Recognition("es", 0.95m), Now.AddSeconds(2));

        Assert.False(first.Accepted);
        Assert.Equal("evidence_accumulating", first.Result);
        Assert.True(second.Accepted);
        Assert.Equal("es-US", second.ActiveLanguageCode);
        Assert.Equal(CallLanguageReasons.AutomaticDetection, second.Reason);
    }

    [Theory]
    [InlineData("sí", "es", 0.99, "not_meaningful")]
    [InlineData("Necesito una cita dental mañana por favor.", "es", 0.30, "low_confidence")]
    public void WeakAutomaticEvidenceDoesNotSwitch(
        string transcript, string detected, decimal confidence, string expectedResult)
    {
        var state = new CallLanguageState("en-US", CallLanguageReasons.LocationDefault, Now);

        CallLanguageDecision decision = state.Evaluate(
            transcript, Recognition(detected, confidence), Now.AddSeconds(1));

        Assert.False(decision.Accepted);
        Assert.Equal(expectedResult, decision.Result);
        Assert.Equal("en-US", state.ActiveLanguageCode);
    }

    [Fact]
    public void NameOrAddressContextDoesNotSwitch()
    {
        var state = new CallLanguageState("en-US", CallLanguageReasons.LocationDefault, Now);

        CallLanguageDecision decision = state.Evaluate(
            "My name is José Hernández Rodríguez from Carolina.", Recognition("es", 0.98m), Now.AddSeconds(1));

        Assert.False(decision.Accepted);
        Assert.Equal("context_fragment", decision.Result);
    }

    [Fact]
    public void MixedLanguageEvidenceDoesNotFlap()
    {
        var state = new CallLanguageState("en-US", CallLanguageReasons.LocationDefault, Now);

        CallLanguageDecision decision = state.Evaluate(
            "I need una cita with the dentist tomorrow.", Recognition(["en", "es"], null), Now.AddSeconds(1));

        Assert.False(decision.Accepted);
        Assert.Equal("mixed_language", decision.Result);
        Assert.Equal("en-US", state.ActiveLanguageCode);
    }

    [Fact]
    public void CooldownAndStrongerEvidencePreventImmediateAutomaticReversal()
    {
        var state = new CallLanguageState("en-US", CallLanguageReasons.LocationDefault, Now);
        _ = state.Evaluate("Necesito hacer una cita dental mañana.", Recognition("es", 0.95m), Now.AddSeconds(1));
        Assert.True(state.Evaluate("Tengo dolor fuerte desde el lunes pasado.", Recognition("es", 0.95m), Now.AddSeconds(2)).Accepted);

        CallLanguageDecision first = state.Evaluate("I need another appointment for next week.", Recognition("en", 0.96m), Now.AddSeconds(3));
        CallLanguageDecision second = state.Evaluate("The tooth pain started several days ago.", Recognition("en", 0.96m), Now.AddSeconds(4));
        CallLanguageDecision third = state.Evaluate("Cold water makes the tooth hurt much more.", Recognition("en", 0.96m), Now.AddSeconds(5));

        Assert.Equal("cooldown", first.Result);
        Assert.Equal("cooldown", second.Result);
        Assert.True(third.Accepted);
        Assert.Equal("en-US", third.ActiveLanguageCode);
    }

    [Fact]
    public void AutomaticSwitchingCanBeDisabledThroughBoundedVoiceConfiguration()
    {
        var state = new CallLanguageState("en-US", CallLanguageReasons.LocationDefault, Now,
            new CallLanguagePolicyOptions(AutomaticSwitchingEnabled: false));

        CallLanguageDecision decision = state.Evaluate(
            "Necesito hacer una cita con el dentista mañana.", Recognition("es", 0.99m), Now.AddSeconds(1));

        Assert.False(decision.Accepted);
        Assert.Equal("automatic_disabled", decision.Result);
        Assert.Equal("en-US", state.ActiveLanguageCode);
    }

    [Fact]
    public void PersistedActiveLanguageCanBeRestoredWithoutLosingStartingLanguage()
    {
        var state = new CallLanguageState("en-US", CallLanguageReasons.LocationDefault, Now);

        state.Restore("es-PR", CallLanguageReasons.AutomaticDetection, Now.AddMinutes(1), 2);

        Assert.Equal("en-US", state.StartingLanguageCode);
        Assert.Equal("es-PR", state.ActiveLanguageCode);
        Assert.Equal(CallLanguageReasons.AutomaticDetection, state.Reason);
        Assert.Equal(2, state.Version);
    }

    [Fact]
    public void InvalidLanguagePolicyBoundsAreRejected()
    {
        _ = Assert.Throws<InvalidOperationException>(() => new CallLanguageState(
            "en-US", CallLanguageReasons.LocationDefault, Now,
            new CallLanguagePolicyOptions(EvidenceThreshold: 1)));
    }

    private static SpeechRecognitionResult Recognition(string? code = null, decimal? confidence = null) =>
        Recognition(code is null ? [] : [code], confidence);

    private static SpeechRecognitionResult Recognition(IReadOnlyList<string> codes, decimal? confidence) => new(
        "recognized", 0.99m, "en-US", Now, Now, true,
        DetectedLanguages: codes, DetectionConfidence: confidence);
}
