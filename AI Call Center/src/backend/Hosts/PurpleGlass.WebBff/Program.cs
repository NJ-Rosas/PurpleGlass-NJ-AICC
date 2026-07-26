using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using PurpleGlass.Application.Abstractions;
using PurpleGlass.Modules.CallManagement.Application;
using PurpleGlass.Modules.CallManagement.Infrastructure;
using PurpleGlass.Modules.Conversation.Application;
using PurpleGlass.Modules.Conversation.Infrastructure;
using PurpleGlass.Modules.Tenancy.Application;
using PurpleGlass.Modules.Tenancy.Contracts;
using PurpleGlass.Modules.Tenancy.Infrastructure;
using PurpleGlass.WebBff;

var builder = WebApplication.CreateBuilder(args);

string connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

PrototypeSessionOptions session = builder.Configuration
    .GetRequiredSection(PrototypeSessionOptions.SectionName)
    .Get<PrototypeSessionOptions>()
    ?? throw new InvalidOperationException("PrototypeSession configuration is required.");

if (session.Enabled && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException("The synthetic prototype session may only run in Development.");
}

builder.Services.AddSingleton(session);
builder.Services.AddScoped<PrototypeRequestContextAccessor>();
builder.Services.AddScoped<IRequestContextAccessor>(provider =>
    provider.GetRequiredService<PrototypeRequestContextAccessor>());
builder.Services.AddTenancyInfrastructure(connectionString);
builder.Services.AddScoped<TenancyService>();
builder.Services.AddCallManagementInfrastructure(connectionString);
builder.Services.AddConversationInfrastructure(connectionString);
builder.Services.AddSingleton<RealtimeEventHub>();
builder.Services.AddHostedService<MqttRealtimeSubscriber>();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("postgres", tags: ["ready"]);
builder.Services.AddExceptionHandler<PrototypeExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseMiddleware<PrototypeRequestContextMiddleware>();

app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") });

RouteGroupBuilder bff = app.MapGroup("/bff/v1");

bff.MapGet("/session", (PrototypeRequestContextAccessor accessor) => Results.Ok(accessor.Current));

bff.MapGet("/tenant-summary", async (TenancyService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.GetCurrentSummaryAsync(cancellationToken)));

bff.MapPut(
    "/locations/{locationId:guid}/display-name",
    async (Guid locationId, UpdateLocationDisplayNameRequest request, TenancyService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.UpdateLocationDisplayNameAsync(locationId, request, cancellationToken)));

bff.MapGet("/calls", async (
    PrototypeRequestContextAccessor accessor,
    CallManagementService calls,
    int? limit,
    CancellationToken cancellationToken) =>
{
    RequestContext context = accessor.Current;
    return Results.Ok(await calls.GetRecentAsync(
        context.TenantId,
        context.LocationId,
        Math.Clamp(limit ?? 20, 1, 50),
        cancellationToken));
});

bff.MapGet("/calls/{callId:guid}", async (
    Guid callId,
    PrototypeRequestContextAccessor accessor,
    CallManagementService calls,
    ConversationService conversations,
    CancellationToken cancellationToken) =>
{
    RequestContext context = accessor.Current;
    var call = await calls.GetForLocationAsync(context.TenantId, context.LocationId, callId, cancellationToken);

    return Results.Ok(new
    {
        call,
        conversation = await conversations.GetDetailsForCallAsync(context.TenantId, callId, cancellationToken)
    });
});

bff.MapGet("/events", async (
    HttpContext httpContext,
    RealtimeEventHub hub,
    PrototypeRequestContextAccessor accessor,
    CancellationToken cancellationToken) =>
{
    httpContext.Response.Headers.CacheControl = "no-cache";
    httpContext.Response.Headers.Connection = "keep-alive";
    httpContext.Response.ContentType = "text/event-stream";

    await using RealtimeSubscription subscription = hub.Subscribe(accessor.Current.TenantId);
    await foreach (RealtimeEvent realtimeEvent in subscription.Reader.ReadAllAsync(cancellationToken))
    {
        await httpContext.Response.WriteAsync($"event: {realtimeEvent.EventType}\n", cancellationToken);
        await httpContext.Response.WriteAsync($"data: {realtimeEvent.Payload}\n\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);
    }
});

app.Run();

public partial class Program;

namespace PurpleGlass.WebBff
{
    public sealed class PrototypeExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
    {
        public async ValueTask<bool> TryHandleAsync(
            HttpContext httpContext,
            Exception exception,
            CancellationToken cancellationToken)
        {
            (int status, string title) = exception switch
            {
                TenancyResourceNotFoundException => (StatusCodes.Status404NotFound, "Tenant resource not found"),
                TenancyConcurrencyException => (StatusCodes.Status409Conflict, "The location was changed by another request"),
                CallApplicationException { Code: "call_not_found" } => (StatusCodes.Status404NotFound, "Call not found"),
                ConversationApplicationException { Code: "conversation_not_found" } => (StatusCodes.Status404NotFound, "Conversation not found"),
                ArgumentException => (StatusCodes.Status400BadRequest, "The request is invalid"),
                _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred")
            };

            httpContext.Response.StatusCode = status;
            return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = new ProblemDetails
                {
                    Status = status,
                    Title = title,
                    Detail = status == StatusCodes.Status500InternalServerError ? null : exception.Message
                },
                Exception = exception
            });
        }
    }
}
