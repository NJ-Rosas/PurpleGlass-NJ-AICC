using Microsoft.EntityFrameworkCore;
using PurpleGlass.Modules.CallManagement.Application;
using PurpleGlass.Modules.CallManagement.Contracts;
using PurpleGlass.Modules.CallManagement.Domain;
using PurpleGlass.Modules.CallManagement.Infrastructure;

namespace PurpleGlass.IntegrationTests;

[Collection(DurablePathGroup.Name)]
public sealed class TelephonyTransportTests(DurablePathFixture fixture)
{
    [Fact]
    public async Task NumberMappingNormalizesAndResolvesTenantLocation()
    {
        Guid tenant = Guid.NewGuid();
        Guid location = Guid.NewGuid();
        await using CallManagementDbContext db = fixture.CreateCalls();
        var service = new CallManagementService(db, TimeProvider.System);
        TelephonyNumberSummary configured = await service.ConfigureTelephonyNumberAsync(new ConfigureTelephonyNumber(
            tenant, location, "Fake", "+1 (787) 555-1200", "PN-test", true, true, true), default);
        Assert.Equal("+17875551200", configured.Number);
        TelephonyNumber? inbound = await db.ResolveInboundNumberAsync("Fake", "+17875551200", default);
        TelephonyNumber? outbound = await db.ResolveOutboundNumberAsync(tenant, location, default);
        Assert.Equal(tenant, inbound?.TenantId.Value);
        Assert.Equal(location, outbound?.LocationId?.Value);
    }

    [Fact]
    public async Task UnknownAndInactiveInboundRoutesAreRejectedSafely()
    {
        await using CallManagementDbContext db = fixture.CreateCalls();
        var service = new CallManagementService(db, TimeProvider.System);
        CallApplicationException unknown = await Assert.ThrowsAsync<CallApplicationException>(() =>
            service.RegisterInboundTransportAsync("Fake", "FA-unknown", null, "+17875550100", "+17875559999", Guid.NewGuid(), default));
        Assert.Equal("telephony_route_not_found", unknown.Code);

        Guid tenant = Guid.NewGuid();
        Guid location = Guid.NewGuid();
        _ = await service.ConfigureTelephonyNumberAsync(new ConfigureTelephonyNumber(
            tenant, location, "Fake", "+17875551201", null, true, false, false), default);
        await Assert.ThrowsAsync<CallApplicationException>(() => service.RegisterInboundTransportAsync(
            "Fake", "FA-inactive", null, "+17875550100", "+17875551201", Guid.NewGuid(), default));
    }

    [Fact]
    public async Task OutboundIntentDispatchAndCallbacksAreDurableAndIdempotent()
    {
        Guid tenant = Guid.NewGuid();
        Guid location = Guid.NewGuid();
        await using CallManagementDbContext db = fixture.CreateCalls();
        var service = new CallManagementService(db, TimeProvider.System);
        _ = await service.ConfigureTelephonyNumberAsync(new ConfigureTelephonyNumber(
            tenant, location, "Fake", "+17875551210", null, true, true, true), default);
        var command = new RequestTransportOutboundCall(tenant, location, "transport-1", "+17875551211", Guid.NewGuid());
        CallSummary call = await service.RequestTransportOutboundAsync(command, default);
        CallSummary replay = await service.RequestTransportOutboundAsync(command, default);
        Assert.Equal(call.CallId, replay.CallId);

        TelephonyOperation operation = await db.TelephonyOperations.SingleAsync(entity => entity.CallId == new CallSessionId(call.CallId));
        operation.BeginDispatch(TimeProvider.System.GetUtcNow());
        await db.SaveChangesAsync();
        await service.CompleteTelephonyDispatchAsync(operation.Id, "FA-provider-call", null, default);
        CallSummary ringing = await service.ApplyProviderStatusAsync(new ApplyProviderCallStatus(
            "Fake", "FA-provider-call", "ringing", "event-ringing"), default);
        CallSummary duplicate = await service.ApplyProviderStatusAsync(new ApplyProviderCallStatus(
            "Fake", "FA-provider-call", "ringing", "event-ringing"), default);
        Assert.Equal("Ringing", ringing.State);
        Assert.Equal(ringing.Version, duplicate.Version);
        CallSummary connected = await service.ApplyProviderStatusAsync(new ApplyProviderCallStatus(
            "Fake", "FA-provider-call", "in-progress", "event-connected"), default);
        CallSummary completed = await service.ApplyProviderStatusAsync(new ApplyProviderCallStatus(
            "Fake", "FA-provider-call", "completed", "event-completed"), default);
        CallSummary stale = await service.ApplyProviderStatusAsync(new ApplyProviderCallStatus(
            "Fake", "FA-provider-call", "ringing", "event-late-ringing"), default);
        Assert.Equal("Answered", connected.State);
        Assert.Equal("Completed", completed.State);
        Assert.Equal(completed.Version, stale.Version);
    }

    [Fact]
    public async Task HangupIsLocationScopedAndIdempotent()
    {
        Guid tenant = Guid.NewGuid();
        Guid location = Guid.NewGuid();
        await using CallManagementDbContext db = fixture.CreateCalls();
        var service = new CallManagementService(db, TimeProvider.System);
        CallSummary call = await service.RegisterInboundAsync(new RegisterInboundCall(
            tenant, location, "FA-hangup", "+17875550100", "+17875550101", Guid.NewGuid(), Provider: "Fake"), default);
        await Assert.ThrowsAsync<CallApplicationException>(() => service.RequestHangupAsync(
            new RequestCallHangup(tenant, Guid.NewGuid(), call.CallId), default));
        _ = await service.RequestHangupAsync(new RequestCallHangup(tenant, location, call.CallId), default);
        _ = await service.RequestHangupAsync(new RequestCallHangup(tenant, location, call.CallId), default);
        Assert.Equal(1, await db.TelephonyOperations.CountAsync(operation => operation.Type == TelephonyOperationType.Hangup));
    }
}
