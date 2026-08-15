using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using PurpleGlass.Eventing;
using PurpleGlass.Modules.Scheduling.Application;
using PurpleGlass.Modules.Scheduling.Domain;

namespace PurpleGlass.Modules.Scheduling.Infrastructure;
public sealed class SchedulingInfrastructureAssembly;
public sealed class SchedulingPersistenceConcurrencyException(Exception inner):Exception("Scheduling state changed concurrently.",inner);

public sealed class SchedulingDbContext(DbContextOptions<SchedulingDbContext> options):DbContext(options),ISchedulingStore
{
    public DbSet<AvailabilityOffer> AvailabilityOffers=>Set<AvailabilityOffer>();
    public DbSet<AppointmentWorkflow> AppointmentWorkflows=>Set<AppointmentWorkflow>();
    public DbSet<AppointmentProjection> AppointmentProjections=>Set<AppointmentProjection>();
    public DbSet<ProviderOperation> ProviderOperations=>Set<ProviderOperation>();
    public DbSet<IdempotencyReceipt> IdempotencyReceipts=>Set<IdempotencyReceipt>();
    public DbSet<OutboxMessage> OutboxMessages=>Set<OutboxMessage>();
    public async Task AddOffersAsync(IEnumerable<AvailabilityOffer> offers,CancellationToken cancellationToken){AvailabilityOffers.AddRange(offers);await SaveChangesAsync(cancellationToken);}
    public Task<AvailabilityOffer?> GetOfferByHashAsync(Guid tenantId,Guid locationId,string tokenHash,bool tracking,CancellationToken cancellationToken)=>Query(AvailabilityOffers,tracking).SingleOrDefaultAsync(x=>x.TenantId==tenantId&&x.LocationId==locationId&&x.TokenHash==tokenHash,cancellationToken);
    public Task<AvailabilityOffer?> GetOfferByIdAsync(Guid tenantId,Guid locationId,Guid offerId,CancellationToken cancellationToken)=>AvailabilityOffers.AsNoTracking().SingleOrDefaultAsync(x=>x.TenantId==tenantId&&x.LocationId==locationId&&x.Id==offerId,cancellationToken);
    public Task<IdempotencyReceipt?> GetReceiptAsync(Guid tenantId,Guid locationId,string operation,string keyHash,CancellationToken cancellationToken)=>IdempotencyReceipts.AsNoTracking().SingleOrDefaultAsync(x=>x.TenantId==tenantId&&x.LocationId==locationId&&x.Operation==operation&&x.KeyHash==keyHash,cancellationToken);
    public Task<AppointmentWorkflow?> GetWorkflowAsync(Guid tenantId,Guid locationId,Guid workflowId,bool tracking,CancellationToken cancellationToken)=>Query(AppointmentWorkflows,tracking).SingleOrDefaultAsync(x=>x.TenantId==tenantId&&x.LocationId==locationId&&x.Id==workflowId,cancellationToken);
    public Task<AppointmentProjection?> GetProjectionAsync(Guid tenantId,Guid locationId,Guid workflowId,CancellationToken cancellationToken)=>AppointmentProjections.AsNoTracking().SingleOrDefaultAsync(x=>x.TenantId==tenantId&&x.LocationId==locationId&&x.WorkflowId==workflowId,cancellationToken);
    public Task<ProviderOperation?> GetNextOperationAsync(DateTimeOffset now,CancellationToken cancellationToken)=>ProviderOperations.OrderBy(x=>x.CreatedAtUtc).FirstOrDefaultAsync(x=>(x.State==ProviderOperationState.Dispatching&&x.Kind==ProviderOperationKind.Book)||(x.State==ProviderOperationState.Pending&&x.NextAttemptAtUtc<=now),cancellationToken);
    public Task<ProviderOperation?> GetOperationAsync(Guid operationId,bool tracking,CancellationToken cancellationToken)=>Query(ProviderOperations,tracking).SingleOrDefaultAsync(x=>x.Id==operationId,cancellationToken);
    public void Add(AppointmentWorkflow workflow)=>AppointmentWorkflows.Add(workflow);public void Add(ProviderOperation workflow)=>ProviderOperations.Add(workflow);public void Add(IdempotencyReceipt workflow)=>IdempotencyReceipts.Add(workflow);public void Add(AppointmentProjection workflow)=>AppointmentProjections.Add(workflow);public void AddOutbox(OutboxMessage message)=>OutboxMessages.Add(message);
    async Task ISchedulingStore.SaveChangesAsync(CancellationToken cancellationToken){try{await SaveChangesAsync(cancellationToken);}catch(DbUpdateConcurrencyException ex){throw new SchedulingPersistenceConcurrencyException(ex);}}
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        Offer(modelBuilder.Entity<AvailabilityOffer>());Workflow(modelBuilder.Entity<AppointmentWorkflow>());Projection(modelBuilder.Entity<AppointmentProjection>());Operation(modelBuilder.Entity<ProviderOperation>());Receipt(modelBuilder.Entity<IdempotencyReceipt>());ConfigureOutbox(modelBuilder.Entity<OutboxMessage>());
    }
    private static IQueryable<T> Query<T>(DbSet<T> set,bool tracking)where T:class=>tracking?set:set.AsNoTracking();
    private static void Offer(EntityTypeBuilder<AvailabilityOffer> e){e.ToTable("availability_offers","scheduling");e.HasKey(x=>x.Id);e.Property(x=>x.AppointmentTypeCode).HasMaxLength(80);e.Property(x=>x.TokenHash).HasMaxLength(64);e.Property(x=>x.OfficeTimeZone).HasMaxLength(100);e.Property(x=>x.SourceVersion).HasMaxLength(120);e.Property(x=>x.ProviderSlotReference).HasMaxLength(200);e.Property(x=>x.ResourceReference).HasMaxLength(200);e.Property(x=>x.Version).IsConcurrencyToken();e.HasIndex(x=>new{x.TenantId,x.LocationId,x.TokenHash}).IsUnique();e.HasIndex(x=>new{x.TenantId,x.LocationId,x.ExpiresAtUtc});}
    private static void Workflow(EntityTypeBuilder<AppointmentWorkflow> e){e.ToTable("appointment_workflows","scheduling");e.HasKey(x=>x.Id);e.Property(x=>x.AppointmentTypeCode).HasMaxLength(80);e.Property(x=>x.PartyReference).HasMaxLength(200);e.Property(x=>x.SemanticFingerprint).HasMaxLength(64);e.Property(x=>x.ResultCode).HasMaxLength(100);e.Property(x=>x.Version).IsConcurrencyToken();e.HasIndex(x=>new{x.TenantId,x.LocationId,x.Id}).IsUnique();e.HasIndex(x=>new{x.State,x.LastTransitionAtUtc});e.HasOne<AvailabilityOffer>().WithMany().HasForeignKey(x=>x.OfferId).OnDelete(DeleteBehavior.Restrict);}
    private static void Projection(EntityTypeBuilder<AppointmentProjection> e){e.ToTable("appointment_projections","scheduling");e.HasKey(x=>x.Id);e.Property(x=>x.ProtectedExternalReference).HasMaxLength(200);e.Property(x=>x.OfficeTimeZone).HasMaxLength(100);e.Property(x=>x.SourceVersion).HasMaxLength(120);e.Property(x=>x.Version).IsConcurrencyToken();e.HasIndex(x=>new{x.TenantId,x.LocationId,x.WorkflowId}).IsUnique();e.HasIndex(x=>new{x.TenantId,x.LocationId,x.ProtectedExternalReference}).IsUnique();}
    private static void Operation(EntityTypeBuilder<ProviderOperation> e){e.ToTable("provider_operations","scheduling");e.HasKey(x=>x.Id);e.Property(x=>x.ResultCode).HasMaxLength(100);e.Property(x=>x.Version).IsConcurrencyToken();e.HasIndex(x=>new{x.State,x.NextAttemptAtUtc});e.HasIndex(x=>new{x.WorkflowId,x.Kind,x.Attempt}).IsUnique();}
    private static void Receipt(EntityTypeBuilder<IdempotencyReceipt> e){e.ToTable("idempotency_receipts","scheduling");e.HasKey(x=>x.Id);e.Property(x=>x.Operation).HasMaxLength(40);e.Property(x=>x.KeyHash).HasMaxLength(64);e.Property(x=>x.SemanticFingerprint).HasMaxLength(64);e.HasIndex(x=>new{x.TenantId,x.LocationId,x.Operation,x.KeyHash}).IsUnique();}
    private static void ConfigureOutbox(EntityTypeBuilder<OutboxMessage> e){e.ToTable("outbox_messages","eventing",t=>t.ExcludeFromMigrations());e.Ignore(x=>x.LeaseId);e.Ignore(x=>x.LeaseExpiresAtUtc);e.HasKey(x=>x.Id);e.Property(x=>x.Topic).HasMaxLength(500);e.Property(x=>x.MessageType).HasMaxLength(200);e.Property(x=>x.Payload).HasColumnType("jsonb");e.Property(x=>x.Producer).HasMaxLength(100);e.Property(x=>x.DataClassification).HasMaxLength(50);e.Property(x=>x.Status).HasMaxLength(30);e.Property(x=>x.TraceId).HasMaxLength(100);e.Property(x=>x.TraceParent).HasMaxLength(100);e.Property(x=>x.TraceState).HasMaxLength(512);e.Property(x=>x.LastError).HasMaxLength(1000);}
}
public sealed class SchedulingDbContextFactory:IDesignTimeDbContextFactory<SchedulingDbContext>{public SchedulingDbContext CreateDbContext(string[] args){var b=new DbContextOptionsBuilder<SchedulingDbContext>();b.UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")??"Host=localhost;Port=5432;Database=purpleglass;Username=purpleglass;Password=purpleglass_dev");return new(b.Options);}}
public static class SchedulingInfrastructureExtensions
{
    public static IServiceCollection AddSchedulingInfrastructure(this IServiceCollection services,string connectionString){services.AddDbContext<SchedulingDbContext>(o=>o.UseNpgsql(connectionString));services.AddScoped<ISchedulingStore>(p=>p.GetRequiredService<SchedulingDbContext>());services.AddScoped<SchedulingService>();services.AddSingleton(TimeProvider.System);return services;}
}
