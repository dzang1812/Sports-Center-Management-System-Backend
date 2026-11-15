using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.API.Controllers;

[ApiController, Authorize, Route("api/me/notifications")]
public sealed class NotificationsController(INotificationService service) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // Đọc thông báo mua, lịch thay đổi và biến động số dư.
    [HttpGet]
    public Task<PagedResponse<NotificationDto>> List(bool unreadOnly = false, int page = 1, int pageSize = 20,
        CancellationToken ct = default) => service.ListAsync(UserId, unreadOnly, page, pageSize, ct);

    // Đánh dấu một thông báo của mình đã đọc.
    [HttpPatch("{id:int}/read")]
    public async Task<IActionResult> MarkRead(int id, CancellationToken ct)
    {
        await service.MarkReadAsync(UserId, id, ct);
        return NoContent();
    }
}
