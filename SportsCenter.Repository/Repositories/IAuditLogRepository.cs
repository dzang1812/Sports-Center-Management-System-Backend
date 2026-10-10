using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

public interface IAuditLogRepository
{
    // Staged on the shared scoped DbContext: persisted by the next SaveChanges of the calling service's other repository.
    void Add(AuditLog log);
    Task<(List<AuditLog> Items, int Total)> SearchAsync(DateTime? from, DateTime? to, string? user, string? action, int page, int pageSize);
}
