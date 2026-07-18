using PurpleGlass.Modules.Audit.Domain;

namespace PurpleGlass.Modules.Audit.Application;

public interface IAuditWriter
{
    void Add(AuditRecord record);
}
