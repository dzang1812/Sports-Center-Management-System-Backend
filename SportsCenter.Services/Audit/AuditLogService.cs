using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Users;

namespace SportsCenter.Services.Audit;

public record AuditLogItem(int Id, DateTime CreatedAt, int? UserId, string? UserName, string Action, string EntityName, string? EntityId, string? Details);

public interface IAuditLogService
{
    Task<PagedResult<AuditLogItem>> ListAsync(DateTime? from, DateTime? to, string? user, string? action, int page, int pageSize);
}

// Read-only: audit rows are written by the services that perform the action.
public class AuditLogService(IAuditLogRepository audit) : IAuditLogService
{
    public async Task<PagedResult<AuditLogItem>> ListAsync(DateTime? from, DateTime? to, string? user, string? action, int page, int pageSize)
    {
        pageSize = pageSize is 10 or 20 or 50 ? pageSize : 20;
        page = Math.Max(page, 1);

        var (logs, total) = await audit.SearchAsync(from, to, user, action, page, pageSize);
        var items = logs.Select(a => new AuditLogItem(a.Id, a.CreatedAt, a.UserId, a.User?.FullName, a.Action, a.EntityName, a.EntityId, a.Details)).ToList();

        return new PagedResult<AuditLogItem>(items, total, page, pageSize);
    }
}
