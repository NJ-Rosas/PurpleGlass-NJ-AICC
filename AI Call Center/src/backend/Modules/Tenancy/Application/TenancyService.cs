using System.Text.Json;
using PurpleGlass.Application.Abstractions;
using PurpleGlass.Eventing;
using PurpleGlass.Modules.Audit.Application;
using PurpleGlass.Modules.Audit.Domain;
using PurpleGlass.Modules.Tenancy.Contracts;
using PurpleGlass.Modules.Tenancy.Domain;

namespace PurpleGlass.Modules.Tenancy.Application;

public sealed class TenancyService(
    ITenancyStore store,
    IAuditWriter auditWriter,
    IRequestContextAccessor requestContextAccessor,
    TimeProvider timeProvider)
{
    public async Task<TenantSummary> GetCurrentSummaryAsync(CancellationToken cancellationToken)
    {
        RequestContext context = requestContextAccessor.Current;

        TenantSummary? summary = await store.GetSummaryAsync(
            new TenantId(context.TenantId),
            new LocationId(context.LocationId),
            cancellationToken);

        return summary ?? throw new TenancyResourceNotFoundException();
    }

    public async Task<TenantSummary> UpdateLocationDisplayNameAsync(
        Guid locationId,
        UpdateLocationDisplayNameRequest request,
        CancellationToken cancellationToken)
    {
        RequestContext context = requestContextAccessor.Current;

        if (locationId != context.LocationId)
        {
            throw new TenancyResourceNotFoundException();
        }

        Location location = await store.GetLocationAsync(
            new TenantId(context.TenantId),
            new LocationId(locationId),
            cancellationToken) ?? throw new TenancyResourceNotFoundException();

        if (location.Version != request.ExpectedVersion)
        {
            throw new TenancyConcurrencyException();
        }

        bool changed = location.Rename(request.DisplayName);
        DateTimeOffset now = timeProvider.GetUtcNow();

        if (changed)
        {
            var integrationEvent = new LocationDisplayNameChanged(
                location.Id.Value,
                location.DisplayName,
                location.Version,
                now);

            store.AddOutbox(OutboxMessage.Create(
                context.TenantId,
                context.LocationId,
                $"pg/local/v1/tenants/{context.TenantId:D}/events/location-display-name-changed",
                nameof(LocationDisplayNameChanged),
                JsonSerializer.Serialize(integrationEvent),
                context.CorrelationId,
                now));

            auditWriter.Add(AuditRecord.Create(
                context.TenantId,
                context.LocationId,
                context.ActorId,
                "LocationDisplayNameChanged",
                "Location",
                location.Id.ToString(),
                "Succeeded",
                context.CorrelationId,
                now));
        }

        await store.SaveChangesAsync(cancellationToken);
        return await GetCurrentSummaryAsync(cancellationToken);
    }
}
