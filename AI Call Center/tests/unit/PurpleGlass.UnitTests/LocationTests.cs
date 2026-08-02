using PurpleGlass.Modules.Tenancy.Domain;

namespace PurpleGlass.UnitTests;

public sealed class LocationTests
{
    [Fact]
    public void RenameNormalizesNameAndIncrementsVersion()
    {
        Location location = CreateLocation();

        bool changed = location.Rename("  Condado Dental  ");

        Assert.True(changed);
        Assert.Equal("Condado Dental", location.DisplayName);
        Assert.Equal(2, location.Version);
    }

    [Fact]
    public void RenameWithCurrentNameDoesNotIncrementVersion()
    {
        Location location = CreateLocation();

        bool changed = location.Rename(" San Juan Prototype Office ");

        Assert.False(changed);
        Assert.Equal(1, location.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RenameWithEmptyNameIsRejected(string displayName)
    {
        Location location = CreateLocation();

        _ = Assert.Throws<ArgumentException>(() => location.Rename(displayName));
    }

    [Fact]
    public void ExistingLocationDefaultsToEnglishCallLanguage()
    {
        Assert.Equal("en-US", CreateLocation().DefaultCallLanguageCode);
    }

    [Fact]
    public void DefaultCallLanguageNormalizesAndUsesLocationVersion()
    {
        Location location = CreateLocation();

        Assert.True(location.ChangeDefaultCallLanguage("es_pr"));
        Assert.Equal("es-PR", location.DefaultCallLanguageCode);
        Assert.Equal(2, location.Version);
        Assert.False(location.ChangeDefaultCallLanguage("es-PR"));
        Assert.Equal(2, location.Version);
    }

    [Fact]
    public void UnsupportedDefaultCallLanguageIsRejected()
    {
        _ = Assert.Throws<ArgumentException>(() => CreateLocation().ChangeDefaultCallLanguage("fr-FR"));
    }

    private static Location CreateLocation() => new(
        new LocationId(Guid.NewGuid()),
        new TenantId(Guid.NewGuid()),
        "San Juan Prototype Office",
        "America/Puerto_Rico");
}
