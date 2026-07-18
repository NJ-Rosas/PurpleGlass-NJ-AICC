using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PurpleGlass.Modules.Audit.Domain;

namespace PurpleGlass.Modules.Audit.Infrastructure;

public static class AuditModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        EntityTypeBuilder<AuditRecord> audit = modelBuilder.Entity<AuditRecord>();
        audit.ToTable("audit_records", "audit");
        audit.HasKey(record => record.Id);
        audit.Property(record => record.ActorId).HasMaxLength(200).IsRequired();
        audit.Property(record => record.Action).HasMaxLength(160).IsRequired();
        audit.Property(record => record.ResourceType).HasMaxLength(100).IsRequired();
        audit.Property(record => record.ResourceId).HasMaxLength(200).IsRequired();
        audit.Property(record => record.Outcome).HasMaxLength(80).IsRequired();
        audit.HasIndex(record => new { record.TenantId, record.OccurredAtUtc });
    }
}
