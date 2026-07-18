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

    private static Location CreateLocation() => new(
        new LocationId(Guid.NewGuid()),
        new TenantId(Guid.NewGuid()),
        "San Juan Prototype Office",
        "America/Puerto_Rico");
}
