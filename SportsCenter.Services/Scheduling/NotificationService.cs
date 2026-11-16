using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;

namespace SportsCenter.Services.Scheduling;

public record NotificationDto(int Id, int? SenderId, NotificationType Type, string Title, string Body,
    bool IsRead, DateTime CreatedAt);

public interface INotificationService
{
    Task<PagedResponse<NotificationDto>> ListAsync(int userId, bool unreadOnly, int page, int pageSize, CancellationToken ct);
    Task MarkReadAsync(int userId, int notificationId, CancellationToken ct);
}

public sealed class NotificationService(INotificationRepository notifications) : INotificationService
{
    // Đọc thông báo của người đang đăng nhập.
    // Nghiệp vụ: Lọc chưa đọc nếu yêu cầu và giới hạn phân trang; không trả thông báo của người khác.
    public async Task<PagedResponse<NotificationDto>> ListAsync(int userId, bool unreadOnly, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = pageSize is 10 or 20 or 50 ? pageSize : 20;
        var (rows, total) = await notifications.ListAsync(userId, unreadOnly, page, pageSize, ct);
        var items = rows.Select(x => new NotificationDto(x.Id, x.SenderId, x.Type, x.Title, x.Body, x.IsRead, x.CreatedAt)).ToList();
        return new(items, total, page, pageSize);
    }

    // Đánh dấu một thông báo đã đọc.
    // Nghiệp vụ: Tìm theo cả mã và chủ sở hữu; đã đọc thì không ghi lại, không có quyền thì báo không tìm thấy.
    public async Task MarkReadAsync(int userId, int notificationId, CancellationToken ct)
    {
        var notification = await notifications.GetOwnedAsync(userId, notificationId, ct)
            ?? throw SchedulingException.NotFound("NOTIFICATION_NOT_FOUND", "Không tìm thấy thông báo.");
        if (notification.IsRead) return;
        notification.IsRead = true;
        await notifications.SaveChangesAsync(ct);
    }
}
