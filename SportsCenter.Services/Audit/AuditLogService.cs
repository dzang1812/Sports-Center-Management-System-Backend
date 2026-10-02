using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Services.Users;

namespace SportsCenter.Services.Audit;

public record AuditLogItem(int Id, DateTime CreatedAt, int? UserId, string? UserName, string Action, string EntityName, string? EntityId, string? Details);

public interface IAuditLogService
{
    Task<PagedResult<AuditLogItem>> ListAsync(DateTime? from, DateTime? to, string? user, string? action, int page, int pageSize);
}

// Read-only: audit rows are written by the services that perform the action.
public class AuditLogService(AppDbContext db) : IAuditLogService
{
    public async Task<PagedResult<AuditLogItem>> ListAsync(DateTime? from, DateTime? to, string? user, string? action, int page, int pageSize)
    {
        pageSize = pageSize is 10 or 20 or 50 ? pageSize : 20;
        page = Math.Max(page, 1);

        var query = db.AuditLogs.AsNoTracking().AsQueryable();
        if (from is { } f) query = query.Where(a => a.CreatedAt >= f.ToUniversalTime());
        if (to is { } t) query = query.Where(a => a.CreatedAt < t.ToUniversalTime().AddDays(1)); // "to" is inclusive by day
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(a => a.Action.Contains(action.Trim()));
        if (!string.IsNullOrWhiteSpace(user))
        {
            var term = user.Trim().ToLower();
            query = query.Where(a => a.User != null && (a.User.FullName.ToLower().Contains(term) || a.User.Email.Contains(term)));
        }

        var total = await query.CountAsync();
        var items = await query.OrderByDescending(a => a.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new AuditLogItem(a.Id, a.CreatedAt, a.UserId, a.User == null ? null : a.User.FullName, a.Action, a.EntityName, a.EntityId, a.Details))
            .ToListAsync();

        return new PagedResult<AuditLogItem>(items, total, page, pageSize);
    }
}
