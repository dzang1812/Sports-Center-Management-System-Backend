using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

public class AuditLogRepository(AppDbContext db) : IAuditLogRepository
{
    public void Add(AuditLog log) => db.AuditLogs.Add(log);

    public async Task<(List<AuditLog> Items, int Total)> SearchAsync(DateTime? from, DateTime? to, string? user, string? action, int page, int pageSize)
    {
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
            .Include(a => a.User).ToListAsync();
        return (items, total);
    }
}
