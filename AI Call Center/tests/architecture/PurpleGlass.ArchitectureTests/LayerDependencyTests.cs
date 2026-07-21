using System.Reflection;
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
