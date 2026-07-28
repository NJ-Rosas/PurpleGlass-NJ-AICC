namespace PurpleGlass.ArchitectureTests;

public sealed class RenderBlueprintTests
{
    [Fact]
    public void Task10BlueprintDeclaresCompatibleNonSecretRuntimeConfiguration()
    {
        Dictionary<string, BlueprintService> services = ParseBlueprint();
        Assert.Equal(["purpleglass-integrations-worker", "purpleglass-web"],
            services.Keys.Order(StringComparer.Ordinal));

        BlueprintService web = services["purpleglass-web"];
        Assert.Equal("true", web.Value("Providers__EnableRealTelephony"));
        Assert.Equal("Twilio", web.Value("Telephony__Provider"));
        Assert.Equal("https://purpleglass-web.onrender.com", web.Value("Telephony__PublicBaseUrl"));
        Assert.Equal("true", web.Value("Providers__EnableRealSpeech"));
        Assert.Equal("OpenAI", web.Value("SpeechToText__Provider"));
        Assert.Equal("OpenAI", web.Value("TextToSpeech__Provider"));
        Assert.Equal("false", web.Value("Providers__EnableRealAI"));
        Assert.Equal("Deterministic", web.Value("LanguageModel__Provider"));

        BlueprintService worker = services["purpleglass-integrations-worker"];
        Assert.Equal("true", worker.Value("Providers__EnableRealTelephony"));
        Assert.Equal("Twilio", worker.Value("Telephony__Provider"));
        Assert.Equal("https://purpleglass-web.onrender.com", worker.Value("Telephony__PublicBaseUrl"));
        Assert.DoesNotContain("Providers__EnableRealSpeech", worker.Variables.Keys);
        Assert.DoesNotContain("Providers__EnableRealAI", worker.Variables.Keys);
    }

    [Fact]
    public void Task10BlueprintDeclaresCredentialsWithoutEmbeddingValues()
    {
        Dictionary<string, BlueprintService> services = ParseBlueprint();
        AssertSecret(services["purpleglass-web"], "OpenAI__ApiKey");
        AssertSecret(services["purpleglass-web"], "Telephony__Twilio__AccountSid");
        AssertSecret(services["purpleglass-web"], "Telephony__Twilio__AuthToken");
        AssertSecret(services["purpleglass-integrations-worker"], "Telephony__Twilio__AccountSid");
        AssertSecret(services["purpleglass-integrations-worker"], "Telephony__Twilio__AuthToken");
    }

    private static void AssertSecret(BlueprintService service, string key)
    {
        BlueprintVariable variable = service.Variables[key];
        Assert.True(variable.SyncFalse);
        Assert.Null(variable.Value);
    }

    private static Dictionary<string, BlueprintService> ParseBlueprint()
    {
        string path = FindRepositoryFile("render.yaml");
        var services = new Dictionary<string, BlueprintService>(StringComparer.Ordinal);
        BlueprintService? service = null;
        string? variableKey = null;
        bool inServices = false;
        bool inVariables = false;

        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (line == "services:") { inServices = true; continue; }
            if (line == "databases:") break;
            if (!inServices || line.Length == 0) continue;
            if (line.StartsWith("- type:", StringComparison.Ordinal))
            {
                service = null;
                variableKey = null;
                inVariables = false;
                continue;
            }
            if (service is null && line.StartsWith("name:", StringComparison.Ordinal))
            {
                string name = Scalar(line);
                service = new BlueprintService(name);
                services.Add(name, service);
                continue;
            }
            if (service is null) continue;
            if (line == "envVars:") { inVariables = true; continue; }
            if (!inVariables) continue;
            if (line.StartsWith("- key:", StringComparison.Ordinal))
            {
                variableKey = Scalar(line);
                service.Variables.Add(variableKey, new BlueprintVariable());
                continue;
            }
            if (variableKey is null) continue;
            if (line.StartsWith("value:", StringComparison.Ordinal))
                service.Variables[variableKey].Value = Scalar(line);
            else if (line.Equals("sync: false", StringComparison.OrdinalIgnoreCase))
                service.Variables[variableKey].SyncFalse = true;
        }
        return services;
    }

    private static string Scalar(string line) => line[(line.IndexOf(':') + 1)..].Trim().Trim('"', '\'');

    private static string FindRepositoryFile(string name)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"Could not locate repository file {name}.");
    }

    private sealed class BlueprintService(string name)
    {
        public string Name { get; } = name;
        public Dictionary<string, BlueprintVariable> Variables { get; } = new(StringComparer.Ordinal);
        public string Value(string key) => Assert.IsType<string>(Variables[key].Value);
    }

    private sealed class BlueprintVariable
    {
        public string? Value { get; set; }
        public bool SyncFalse { get; set; }
    }
}
