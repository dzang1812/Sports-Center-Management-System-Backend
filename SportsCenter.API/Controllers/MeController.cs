using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Services.Auth;
using SportsCenter.Services.Profile;

namespace SportsCenter.API.Controllers;

[ApiController, Authorize]
[Route("api/me")]
public class MeController(IMeService me) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<MeResponse>> Get() => await me.GetAsync(UserId);

    [HttpPut]
    public async Task<ActionResult<MeResponse>> Update(UpdateMeRequest request) => await me.UpdateAsync(UserId, request);

    [HttpPut("password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request)
    {
        try
        {
            await me.ChangePasswordAsync(UserId, request);
            return NoContent();
        }
        catch (AuthException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
