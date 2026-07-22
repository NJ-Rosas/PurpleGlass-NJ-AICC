using System.Reflection;
using PurpleGlass.Adapters.AI.Mock;
using PurpleGlass.Adapters.Speech.Mock;
using PurpleGlass.CallOrchestrator.Worker;
using PurpleGlass.Modules.Audit.Application;
using PurpleGlass.Modules.Audit.Domain;
using PurpleGlass.Modules.CallManagement.Application;
using PurpleGlass.Modules.CallManagement.Contracts;
using PurpleGlass.Modules.CallManagement.Domain;
using PurpleGlass.Modules.CallManagement.Infrastructure;
using PurpleGlass.Modules.Conversation.Application;
using PurpleGlass.Modules.Conversation.Contracts;
using PurpleGlass.Modules.Conversation.Domain;
using PurpleGlass.Modules.Conversation.Infrastructure;
using PurpleGlass.Modules.Tenancy.Application;
using PurpleGlass.Modules.Tenancy.Domain;

namespace PurpleGlass.ArchitectureTests;

public sealed class LayerDependencyTests
{
    private static readonly Assembly[] DomainAssemblies =
    [
        typeof(TenancyDomainAssembly).Assembly,
        typeof(AuditDomainAssembly).Assembly,
        typeof(CallManagementDomainAssembly).Assembly,
        typeof(ConversationDomainAssembly).Assembly,
    ];

    private static readonly Assembly[] ApplicationAssemblies =
    [
        typeof(TenancyApplicationAssembly).Assembly,
        typeof(AuditApplicationAssembly).Assembly,
        typeof(CallManagementApplicationAssembly).Assembly,
        typeof(ConversationApplicationAssembly).Assembly,
    ];

    private static readonly Assembly[] InfrastructureAssemblies =
    [
        typeof(CallManagementInfrastructureAssembly).Assembly,
        typeof(ConversationInfrastructureAssembly).Assembly,
    ];

    private static readonly Assembly[] ContractAssemblies =
    [
        typeof(CallReceived).Assembly,
        typeof(ConversationStarted).Assembly,
    ];

    [Fact]
    public void DomainAssembliesDoNotReferenceOuterLayersOrTechnologies()
    {
        string[] forbiddenReferences =
        [
            ".Application",
            ".Infrastructure",
            ".Presentation",
            "Microsoft.AspNetCore",
            "Microsoft.EntityFrameworkCore",
            "MQTTnet",
        ];

        AssertAssembliesDoNotReference(DomainAssemblies, forbiddenReferences);
    }

    [Fact]
    public void ApplicationAssembliesDoNotReferenceInfrastructureOrTransport()
    {
        string[] forbiddenReferences =
        [
            ".Infrastructure",
            ".Presentation",
            "Microsoft.AspNetCore",
            "Microsoft.EntityFrameworkCore",
            "MQTTnet",
        ];

        AssertAssembliesDoNotReference(ApplicationAssemblies, forbiddenReferences);
    }

    [Fact]
    public void InfrastructureAssembliesDoNotReferencePresentationHostsOrProviders()
    {
        string[] forbiddenReferences = [".Presentation", ".Hosts", "MQTTnet"];
        AssertAssembliesDoNotReference(InfrastructureAssemblies, forbiddenReferences);
    }

    [Fact]
    public void ContractsDoNotExposePersistenceTechnologies()
    {
        string[] forbiddenReferences = ["Microsoft.EntityFrameworkCore", "Npgsql"];
        AssertAssembliesDoNotReference(ContractAssemblies, forbiddenReferences);
    }

    [Fact]
    public void ConversationDomainDoesNotReferenceCallManagementDomain()
    {
        string[] references = typeof(ConversationDomainAssembly).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty).ToArray();
        Assert.DoesNotContain("PurpleGlass.Modules.CallManagement.Domain", references);
    }

    [Fact]
    public void OrchestrationWorkflowDoesNotAcceptInfrastructureOrDbContextDependencies()
    {
        Type[] dependencies = typeof(CallOrchestrationService).GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(dependencies, type => type.Name.EndsWith("DbContext", StringComparison.Ordinal));
        Assert.DoesNotContain(dependencies, type => type.Namespace?.Contains(".Infrastructure", StringComparison.Ordinal) == true);
        Assert.Contains(typeof(CallManagementService), dependencies);
        Assert.Contains(typeof(ConversationService), dependencies);
    }

    [Fact]
    public void MockAdaptersImplementProviderNeutralPortsWithoutPersistenceReferences()
    {
        Assert.True(typeof(IAiConversationRuntime).IsAssignableFrom(typeof(MockAiConversationRuntime)));
        Assert.True(typeof(ISpeechRecognizer).IsAssignableFrom(typeof(MockSpeechRecognizer)));
        Assert.True(typeof(ISpeechSynthesizer).IsAssignableFrom(typeof(MockSpeechSynthesizer)));

        AssertAssembliesDoNotReference(
            [typeof(MockAiAdapterAssembly).Assembly, typeof(MockSpeechAdapterAssembly).Assembly],
            ["Microsoft.EntityFrameworkCore", "Npgsql", ".Infrastructure"]);
    }

    [Fact]
    public void ProviderSdkTypesAreNotExposedByApplicationOrDomainAssemblies()
    {
        AssertAssembliesDoNotReference(
            ApplicationAssemblies.Concat(DomainAssemblies),
            ["OpenAI", "Anthropic", "Azure.AI", "Whisper", "ElevenLabs", "Twilio"]);
    }

    [Fact]
    public void DomainAssembliesDoNotReferenceCallOrchestrator()
    {
        AssertAssembliesDoNotReference(DomainAssemblies, ["PurpleGlass.CallOrchestrator"]);
    }

    [Fact]
    public void SimulatorDelegatesWorkflowToCallOrchestrator()
    {
        Assembly simulator = Assembly.Load("PurpleGlass.CallSimulator");
        string[] references = simulator.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.Contains("PurpleGlass.CallOrchestrator.Worker", references);
        Assert.DoesNotContain(references, reference => reference.EndsWith(".Domain", StringComparison.Ordinal));
        Assert.DoesNotContain(references, reference => reference.EndsWith(".Infrastructure", StringComparison.Ordinal));
    }

    private static void AssertAssembliesDoNotReference(
        IEnumerable<Assembly> assemblies,
        IEnumerable<string> forbiddenReferences)
    {
        foreach (Assembly assembly in assemblies)
        {
            string[] references = assembly
                .GetReferencedAssemblies()
                .Select(reference => reference.Name ?? string.Empty)
                .ToArray();

            foreach (string forbiddenReference in forbiddenReferences)
            {
                Assert.DoesNotContain(
                    references,
                    reference => reference.Contains(forbiddenReference, StringComparison.Ordinal));
            }
        }
    }
}
