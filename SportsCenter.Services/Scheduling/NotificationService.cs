using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Services.Scheduling;

public record NotificationDto(int Id, int? SenderId, NotificationType Type, string Title, string Body,
    bool IsRead, DateTime CreatedAt);

public interface INotificationService
{
    Task<PagedResponse<NotificationDto>> ListAsync(int userId, bool unreadOnly, int page, int pageSize, CancellationToken ct);
    Task MarkReadAsync(int userId, int notificationId, CancellationToken ct);
}

public sealed class NotificationService(AppDbContext db) : INotificationService
{
    public async Task<PagedResponse<NotificationDto>> ListAsync(int userId, bool unreadOnly, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = pageSize is 10 or 20 or 50 ? pageSize : 20;
        var query = db.Notifications.AsNoTracking().Where(x => x.UserId == userId);
        if (unreadOnly) query = query.Where(x => !x.IsRead);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new NotificationDto(x.Id, x.SenderId, x.Type, x.Title, x.Body, x.IsRead, x.CreatedAt))
            .ToListAsync(ct);
        return new(items, total, page, pageSize);
    }

    public async Task MarkReadAsync(int userId, int notificationId, CancellationToken ct)
    {
        var notification = await db.Notifications.FirstOrDefaultAsync(x => x.Id == notificationId && x.UserId == userId, ct)
            ?? throw SchedulingException.NotFound("NOTIFICATION_NOT_FOUND", "Không tìm thấy thông báo.");
        if (notification.IsRead) return;
        notification.IsRead = true;
        await db.SaveChangesAsync(ct);
    }
}
