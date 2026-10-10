using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

public interface INotificationRepository
{
    void Add(Notification notification);
    Task<(List<Notification> Items, int Total)> ListAsync(int userId, bool unreadOnly, int page, int pageSize, CancellationToken ct);
    Task<Notification?> GetOwnedAsync(int userId, int notificationId, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

public class NotificationRepository(AppDbContext db) : INotificationRepository
{
    public void Add(Notification notification) => db.Notifications.Add(notification);

    public async Task<(List<Notification> Items, int Total)> ListAsync(int userId, bool unreadOnly, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Notifications.AsNoTracking().Where(x => x.UserId == userId);
        if (unreadOnly) query = query.Where(x => !x.IsRead);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    public Task<Notification?> GetOwnedAsync(int userId, int notificationId, CancellationToken ct) =>
        db.Notifications.FirstOrDefaultAsync(x => x.Id == notificationId && x.UserId == userId, ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
