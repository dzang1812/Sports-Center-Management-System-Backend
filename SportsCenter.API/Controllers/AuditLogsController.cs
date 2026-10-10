using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Services.Audit;
using SportsCenter.Services.Users;

namespace SportsCenter.API.Controllers;

[ApiController, Authorize(Roles = "Manager")]
[Route("api/audit-logs")]
public class AuditLogsController(IAuditLogService audit) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<AuditLogItem>> List(DateTime? from, DateTime? to, string? user, string? action, int page = 1, int pageSize = 20) =>
        audit.ListAsync(from, to, user, action, page, pageSize);
}
