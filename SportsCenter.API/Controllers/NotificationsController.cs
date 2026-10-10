using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.API.Controllers;

[ApiController, Authorize, Route("api/me/notifications")]
public sealed class NotificationsController(INotificationService service) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public Task<PagedResponse<NotificationDto>> List(bool unreadOnly = false, int page = 1, int pageSize = 20,
        CancellationToken ct = default) => service.ListAsync(UserId, unreadOnly, page, pageSize, ct);

    [HttpPatch("{id:int}/read")]
    public async Task<IActionResult> MarkRead(int id, CancellationToken ct)
    {
        await service.MarkReadAsync(UserId, id, ct);
        return NoContent();
    }
}
