using System.Diagnostics;
using PurpleGlass.Adapters.AI.Mock;
using PurpleGlass.Adapters.AI.OpenAI;
using PurpleGlass.Adapters.Speech.Mock;
using PurpleGlass.Adapters.Speech.OpenAI;
using PurpleGlass.Adapters.Telephony.Twilio;
using PurpleGlass.Modules.Conversation.Application;

namespace PurpleGlass.WebBff;

public static class RealtimeVoiceServiceCollectionExtensions
{
    public static IServiceCollection AddRealtimeVoice(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        string speechToText = Provider(configuration, "SpeechToText");
        string languageModel = Provider(configuration, "LanguageModel");
        string textToSpeech = Provider(configuration, "TextToSpeech");
        RealtimeVoiceOptions configured = configuration.GetRequiredSection(RealtimeVoiceOptions.SectionName)
            .Get<RealtimeVoiceOptions>()
            ?? throw new InvalidOperationException("Voice configuration is required.");
        RealtimeVoiceOptions options = configured with
        {
            Conversation = configured.Conversation with
            {
                AiAdapterKey = AiAdapterKey(languageModel),
                SpeechRecognitionAdapterKey = SpeechAdapterKey(speechToText),
                SpeechSynthesisAdapterKey = SpeechAdapterKey(textToSpeech),
            },
        };
        options.Validate();
        services.AddSingleton(RealtimeVoiceRuntimeStatus.From(
            options.Enabled, speechToText, textToSpeech));

        bool realAiEnabled = configuration.GetValue<bool>("Providers:EnableRealAI");
        bool realSpeechEnabled = configuration.GetValue<bool>("Providers:EnableRealSpeech");
        if (languageModel == "OpenAI" && !realAiEnabled)
            throw new InvalidOperationException("voice_provider_configuration_invalid: OpenAI language model requires Providers:EnableRealAI=true.");
        if ((speechToText == "OpenAI" || textToSpeech == "OpenAI") && !realSpeechEnabled)
            throw new InvalidOperationException("voice_provider_configuration_invalid: OpenAI speech requires Providers:EnableRealSpeech=true.");

        services.AddSingleton(options);
        services.AddSingleton<IVoiceSessionStateSink, BffVoiceSessionStateSink>();
        services.AddScoped<RealtimeVoiceSession>();
        services.AddSingleton<VoiceSessionManager>();
        services.AddSingleton(new TwilioRealtimeAudioOptions().Validate());
        services.AddSingleton<TwilioRealtimeAudioTransportFactory>();

        services.AddSingleton(new MockAiOptions());
        services.AddSingleton(new MockSpeechOptions());
        services.AddSingleton<MockAiConversationRuntime>();
        services.AddSingleton<MockSpeechRecognizer>();
        services.AddSingleton<MockSpeechSynthesizer>();
        services.AddSingleton<DisabledAiConversationRuntime>();
        services.AddSingleton<DisabledSpeechProvider>();

        if (languageModel == "OpenAI") AddOpenAiConversation(services, configuration);
        if (speechToText == "OpenAI" || textToSpeech == "OpenAI") AddOpenAiSpeech(services, configuration);

        services.AddTransient<IAiConversationRuntime>(provider => languageModel switch
        {
            "Fake" => provider.GetRequiredService<MockAiConversationRuntime>(),
            "OpenAI" => provider.GetRequiredService<OpenAiConversationRuntime>(),
            "Disabled" => provider.GetRequiredService<DisabledAiConversationRuntime>(),
            _ => throw new UnreachableException(),
        });
        services.AddTransient<ISpeechRecognizer>(provider => speechToText switch
        {
            "Fake" => provider.GetRequiredService<MockSpeechRecognizer>(),
            "OpenAI" => provider.GetRequiredService<OpenAiSpeechRecognizer>(),
            "Disabled" => provider.GetRequiredService<DisabledSpeechProvider>(),
            _ => throw new UnreachableException(),
        });
        services.AddTransient<ISpeechSynthesizer>(provider => textToSpeech switch
        {
            "Fake" => provider.GetRequiredService<MockSpeechSynthesizer>(),
            "OpenAI" => provider.GetRequiredService<OpenAiSpeechSynthesizer>(),
            "Disabled" => provider.GetRequiredService<DisabledSpeechProvider>(),
            _ => throw new UnreachableException(),
        });
        return services;
    }

    private static void AddOpenAiConversation(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new OpenAiConversationOptions
        {
            ApiKey = configuration["OpenAI:ApiKey"] ?? string.Empty,
            BaseUri = BaseUri(configuration),
            Model = configuration["OpenAI:LanguageModel"] ?? string.Empty,
        }.Validate());
        services.AddHttpClient<OpenAiConversationRuntime>(client => client.Timeout = Timeout.InfiniteTimeSpan);
    }

    private static void AddOpenAiSpeech(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new OpenAiSpeechOptions
        {
            ApiKey = configuration["OpenAI:ApiKey"] ?? string.Empty,
            BaseUri = BaseUri(configuration),
            TranscriptionModel = configuration["OpenAI:TranscriptionModel"] ?? string.Empty,
            SynthesisModel = configuration["OpenAI:SpeechModel"] ?? string.Empty,
        }.Validate());
        services.AddHttpClient<OpenAiSpeechRecognizer>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient<OpenAiSpeechSynthesizer>(client => client.Timeout = Timeout.InfiniteTimeSpan);
    }

    private static Uri BaseUri(IConfiguration configuration) =>
        Uri.TryCreate(configuration["OpenAI:BaseUrl"], UriKind.Absolute, out Uri? value)
            ? value : throw new InvalidOperationException("voice_provider_configuration_invalid: OpenAI base URL is invalid.");

    private static string Provider(IConfiguration configuration, string section)
    {
        string configured = configuration[$"{section}:Provider"]?.Trim() ?? "Disabled";
        return configured.ToLowerInvariant() switch
        {
            "fake" => "Fake",
            "openai" => "OpenAI",
            "disabled" or "none" => "Disabled",
            _ => throw new InvalidOperationException($"voice_provider_configuration_invalid: unsupported {section} provider."),
        };
    }

    private static string AiAdapterKey(string provider) => provider switch
    {
        "Fake" => "mock-ai",
        "OpenAI" => "openai",
        "Disabled" => "disabled",
        _ => throw new UnreachableException(),
    };

    private static string SpeechAdapterKey(string provider) => provider switch
    {
        "Fake" => "mock-speech",
        "OpenAI" => "openai",
        "Disabled" => "disabled",
        _ => throw new UnreachableException(),
    };
}
