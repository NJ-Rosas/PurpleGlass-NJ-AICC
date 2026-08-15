using PurpleGlass.Modules.Scheduling.Application;
using PurpleGlass.Modules.Scheduling.Domain;
using PurpleGlass.Application.Abstractions;
using PurpleGlass.Eventing.Infrastructure;
using PurpleGlass.Modules.Audit.Application;
using PurpleGlass.Modules.Scheduling.Contracts;
namespace PurpleGlass.Integrations.Worker;

public sealed class WorkerSchedulingRequestContextAccessor:IRequestContextAccessor
{
    public RequestContext Current=>throw new InvalidOperationException("The integrations worker cannot initiate Scheduling user commands.");
}

public sealed class SchedulingAuditEvidenceConsumer(InboxDeduplicationStore inbox,SecurityAuditService audit)
{
    public Task<bool> ConsumeAsync(Guid messageId,Guid tenantId,Guid locationId,string actorId,Guid correlationId,SchedulingAuditEvidence evidence,CancellationToken cancellationToken)=>
        inbox.ExecuteOnceAsync("audit.scheduling.v1",messageId,tenantId,
            ct=>audit.WriteAsync(tenantId,locationId,actorId,evidence.Action,"appointment_workflow",evidence.WorkflowId.ToString("D"),evidence.Outcome,evidence.ReasonCode,correlationId,ct),cancellationToken);
}

public sealed class SchedulingProviderWorker(SchedulingDispatchProcessor processor,TimeProvider timeProvider):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {while(!stoppingToken.IsCancellationRequested){if(!await processor.ProcessNextAsync(stoppingToken))await Task.Delay(TimeSpan.FromMilliseconds(500),timeProvider,stoppingToken);}}
}
public sealed partial class SchedulingDispatchProcessor(IServiceScopeFactory scopeFactory,IPracticeManagementSystem provider,ILogger<SchedulingDispatchProcessor> logger)
{
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        SchedulingDispatch? dispatch;
        await using(AsyncServiceScope scope=scopeFactory.CreateAsyncScope())dispatch=await scope.ServiceProvider.GetRequiredService<SchedulingService>().BeginNextDispatchAsync(cancellationToken);
        if(dispatch is null)return false;
        if(dispatch.Kind==ProviderOperationKind.Book)
        {
            ProviderBookingResult result;
            try{result=await provider.BookAppointmentAsync(new(dispatch.OperationId,dispatch.WorkflowId,dispatch.TenantId,dispatch.LocationId,dispatch.SlotReference,dispatch.PartyReference,dispatch.AppointmentTypeCode,dispatch.StartUtc,dispatch.EndUtc,dispatch.OfficeTimeZone,dispatch.SourceVersion),cancellationToken);}
            catch(Exception ex)when(ex is not OperationCanceledException){LogProviderFailure(logger,dispatch.OperationId,"outcome_unknown");result=new(ProviderBookingCategory.OutcomeUnknown,"outcome_unknown");}
            await using AsyncServiceScope completeScope=scopeFactory.CreateAsyncScope();await completeScope.ServiceProvider.GetRequiredService<SchedulingService>().ApplyBookingResultAsync(dispatch.OperationId,result,cancellationToken);
        }
        else
        {
            ProviderReconciliationResult result;
            try{result=await provider.ReconcileBookingAsync(new(dispatch.OperationId,dispatch.WorkflowId,dispatch.TenantId,dispatch.LocationId),cancellationToken);}
            catch(Exception ex)when(ex is not OperationCanceledException){LogProviderFailure(logger,dispatch.OperationId,"reconciliation_unknown");result=new(ProviderReconciliationCategory.StillUnknown,"still_unknown");}
            await using AsyncServiceScope completeScope=scopeFactory.CreateAsyncScope();await completeScope.ServiceProvider.GetRequiredService<SchedulingService>().ApplyReconciliationResultAsync(dispatch.OperationId,result,cancellationToken);
        }
        return true;
    }
    [LoggerMessage(EventId=40,Level=LogLevel.Warning,Message="Scheduling provider operation {OperationId} returned bounded category {ResultCode}.")]
    private static partial void LogProviderFailure(ILogger logger,Guid operationId,string resultCode);
}
