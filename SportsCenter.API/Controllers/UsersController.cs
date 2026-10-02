using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Repository.Entities;
using SportsCenter.Services.Auth;
using SportsCenter.Services.Users;

namespace SportsCenter.API.Controllers;

[ApiController, Authorize(Roles = "Manager")]
[Route("api/users")]
public class UsersController(IUserAdminService admin) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public Task<PagedResult<UserListItem>> List(UserRole? role, UserStatus? status, string? q, int page = 1, int pageSize = 10) =>
        admin.ListAsync(role, status, q, page, pageSize);

    [HttpPost]
    public Task<IActionResult> Create(CreateUserRequest request) =>
        Run(async () => Ok(await admin.CreateAsync(ActorId, request)));

    [HttpPut("{id:int}")]
    public Task<IActionResult> Update(int id, UpdateUserRequest request) =>
        Run(async () => Ok(await admin.UpdateAsync(ActorId, id, request)));

    [HttpPatch("{id:int}/status")]
    public Task<IActionResult> SetStatus(int id, SetStatusRequest request) =>
        Run(async () =>
        {
            await admin.SetStatusAsync(ActorId, id, request.IsActive);
            return NoContent();
        });

    // Expected business failures become 400 with { error }.
    private static async Task<IActionResult> Run(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (AuthException ex) { return new BadRequestObjectResult(new { error = ex.Message }); }
    }
}
