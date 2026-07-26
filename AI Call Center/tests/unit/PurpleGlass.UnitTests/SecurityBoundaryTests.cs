using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using PurpleGlass.Modules.Identity.Application;
using PurpleGlass.Modules.Identity.Domain;
using PurpleGlass.WebBff;

namespace PurpleGlass.UnitTests;

public sealed class SecurityBoundaryTests
{
    [Theory]
    [InlineData(MembershipRole.TenantAdministrator, SecurityPermissions.ManageTenantSettings, true)]
    [InlineData(MembershipRole.TenantAdministrator, SecurityPermissions.InitiateOutboundCalls, true)]
    [InlineData(MembershipRole.OfficeManager, SecurityPermissions.ManageTenantSettings, false)]
    [InlineData(MembershipRole.OfficeManager, SecurityPermissions.ManageLocationSettings, true)]
    [InlineData(MembershipRole.StaffUser, SecurityPermissions.ViewRecentCalls, true)]
    [InlineData(MembershipRole.StaffUser, SecurityPermissions.InitiateOutboundCalls, true)]
    [InlineData(MembershipRole.ReadOnlyUser, SecurityPermissions.ViewTranscripts, true)]
    [InlineData(MembershipRole.ReadOnlyUser, SecurityPermissions.InitiateOutboundCalls, false)]
    public void RolePermissionMatrixIsExplicit(MembershipRole role, string permission, bool expected) =>
        Assert.Equal(expected, SecurityPermissions.ForRole(role).Contains(permission));

    [Fact]
    public async Task DevelopmentDirectoryRejectsUnknownIdentity()
    {
        var service = new IdentityAuthorizationService(new DevelopmentIdentityDirectory());
        IdentitySecurityException exception = await Assert.ThrowsAsync<IdentitySecurityException>(() =>
            service.RequireActiveAsync("unknown", null, default));
        Assert.Equal("authentication_required", exception.Code);
    }

    [Fact]
    public async Task DevelopmentDirectoryRejectsUnauthorizedLocation()
    {
        var service = new IdentityAuthorizationService(new DevelopmentIdentityDirectory());
        IdentitySecurityException exception = await Assert.ThrowsAsync<IdentitySecurityException>(() =>
            service.RequireActiveAsync(DevelopmentIdentityDirectory.AdministratorSubject, Guid.NewGuid(), default));
        Assert.Equal("location_access_denied", exception.Code);
    }

    [Fact]
    public void ProductionRejectsDevelopmentAuthentication()
    {
        SecurityOptions options = WithDevelopmentAuthentication();
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionSecurityValidator.Validate(options, new SafetyOptions(), new TestEnvironment("Production"), "app.example.test"));
        Assert.Contains("production_security_configuration_invalid", exception.Message, StringComparison.Ordinal);

        static SecurityOptions WithDevelopmentAuthentication() => new()
        {
            AllowDevelopmentAuthentication = true,
            AllowSyntheticDataOnly = true,
            RequireHttps = true,
            ProductionOrigin = "https://app.example.test",
            DataProtectionKeysPath = "/keys",
            Oidc = new() { Authority = "https://identity.example.test", ClientId = "client", ClientSecret = "reference", CallbackPath = "/signin-oidc" }
        };
    }

    [Fact]
    public void ProductionRejectsMissingIdentityConfiguration()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionSecurityValidator.Validate(new SecurityOptions(), new SafetyOptions(), new TestEnvironment("Production"), "app.example.test"));
        Assert.Contains("OpenID Connect", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionRejectsBroadAllowedHosts()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionSecurityValidator.Validate(ValidProduction(), new SafetyOptions(), new TestEnvironment("Production"), "*"));
        Assert.Contains("AllowedHosts", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionRejectsProviderOrSensitiveDataSwitches()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionSecurityValidator.Validate(ValidProduction(), new SafetyOptions { EnableRealAI = true }, new TestEnvironment("Production"), "app.example.test"));
        Assert.Contains("external providers", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionAcceptsCompleteSafeConfiguration() =>
        ProductionSecurityValidator.Validate(
            ValidProduction(), new SafetyOptions(), new TestEnvironment("Production"), "app.example.test");

    private static SecurityOptions ValidProduction() => new()
    {
        AllowSyntheticDataOnly = true,
        RequireHttps = true,
        ProductionOrigin = "https://app.example.test",
        DataProtectionKeysPath = "/keys",
        Oidc = new() { Authority = "https://identity.example.test", ClientId = "client", ClientSecret = "reference", CallbackPath = "/signin-oidc" },
        IdentityMappings = [new() { UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), MembershipId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), ExternalSubject = "subject", DisplayName = "User", TenantId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), LocationIds = [Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd")], ActiveLocationId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"), Role = "StaffUser" }]
    };

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
