namespace PurpleGlass.Application.Abstractions;

public sealed record RequestContext(
    Guid TenantId,
    Guid LocationId,
    string ActorId,
    string Role,
    Guid CorrelationId);

public interface IRequestContextAccessor
{
    RequestContext Current { get; }
}
