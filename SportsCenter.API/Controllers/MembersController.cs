using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Repository.Entities;
using SportsCenter.Services.Users;

namespace SportsCenter.API.Controllers;

// Read-only member directory for the front desk. Always scoped to Role=Member regardless of query,
// so Receptionist gets a search-only view — not the full Manager user-admin surface (create/edit/lock).
[ApiController, Authorize(Roles = "Receptionist,Manager"), Route("api/members")]
public class MembersController(IUserAdminService admin) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<UserListItem>> List(string? q, int page = 1, int pageSize = 10) =>
        admin.ListAsync(UserRole.Member, null, q, page, pageSize);
}
