using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Services.Membership;

namespace SportsCenter.API.Controllers;

[ApiController, Route("api")]
public sealed class MemberTiersController(MemberTierService service) : ControllerBase
{
    private int Actor => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpGet("me/tier"), Authorize(Roles = "Member")]
    public Task<MemberTierDto> Mine(CancellationToken ct) => service.GetAsync(Actor, Actor, ct);
    [HttpGet("members/{memberId:int}/tier"), Authorize(Roles = "Manager,Receptionist")]
    public Task<MemberTierDto> ForMember(int memberId, CancellationToken ct) => service.GetAsync(Actor, memberId, ct);
}
