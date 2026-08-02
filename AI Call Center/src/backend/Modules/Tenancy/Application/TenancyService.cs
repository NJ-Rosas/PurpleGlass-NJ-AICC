using System.Text.Json;
using PurpleGlass.Application.Abstractions;
using PurpleGlass.Eventing;
using PurpleGlass.Modules.Audit.Application;
using PurpleGlass.Modules.Audit.Domain;
using PurpleGlass.Modules.Tenancy.Contracts;
using PurpleGlass.Modules.Tenancy.Domain;
using PurpleGlass.SharedKernel;

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

    public async Task<TenantSummary> UpdateLocationDefaultCallLanguageAsync(
        Guid locationId,
        UpdateLocationDefaultCallLanguageRequest request,
        CancellationToken cancellationToken)
    {
        RequestContext context = requestContextAccessor.Current;
        if (locationId != context.LocationId) throw new TenancyResourceNotFoundException();
        Location location = await store.GetLocationAsync(new TenantId(context.TenantId), new LocationId(locationId), cancellationToken)
            ?? throw new TenancyResourceNotFoundException();
        if (location.Version != request.ExpectedVersion) throw new TenancyConcurrencyException();

        string previous = location.DefaultCallLanguageCode;
        string normalized;
        try { normalized = SupportedCallLanguages.Require(request.LanguageCode, nameof(request.LanguageCode)).Code; }
        catch (ArgumentException exception) { throw new TenancyValidationException("unsupported_call_language", exception); }
        bool changed = location.ChangeDefaultCallLanguage(normalized);
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (changed)
        {
            var integrationEvent = new LocationDefaultCallLanguageChanged(
                location.Id.Value, previous, location.DefaultCallLanguageCode, location.Version, now);
            store.AddOutbox(OutboxMessage.Create(
                context.TenantId, context.LocationId,
                $"pg/local/v1/tenants/{context.TenantId:D}/events/location-default-call-language-changed",
                nameof(LocationDefaultCallLanguageChanged), JsonSerializer.Serialize(integrationEvent),
                context.CorrelationId, now));
            auditWriter.Add(AuditRecord.Create(
                context.TenantId, context.LocationId, context.ActorId,
                "LocationDefaultCallLanguageChanged", "Location", location.Id.ToString(),
                $"Succeeded:{AuditCode(previous)}_to_{AuditCode(location.DefaultCallLanguageCode)}",
                context.CorrelationId, now));
        }
        await store.SaveChangesAsync(cancellationToken);
        return await GetCurrentSummaryAsync(cancellationToken);
    }

    private static string AuditCode(string code) => code.Replace('-', '_').ToLowerInvariant();
}
