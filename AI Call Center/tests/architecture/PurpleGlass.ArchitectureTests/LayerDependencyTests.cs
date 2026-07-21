using System.Reflection;
using PurpleGlass.Modules.Audit.Application;
using PurpleGlass.Modules.Audit.Domain;
using PurpleGlass.Modules.CallManagement.Domain;
using PurpleGlass.Modules.Conversation.Domain;
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
