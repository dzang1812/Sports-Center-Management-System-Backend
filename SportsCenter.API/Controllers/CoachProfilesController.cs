using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Services.Profile;

namespace SportsCenter.API.Controllers;

[ApiController, Authorize, Route("api/coaches")]
public sealed class CoachProfilesController(CoachProfileService service) : ControllerBase
{
    private int Actor => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpGet("{coachId:int}/profile"), Authorize(Roles = "Member,Receptionist,Manager,Coach")]
    public Task<CoachProfileResponse> Get(int coachId, CancellationToken ct) => service.GetAsync(Actor, coachId, ct);
}

[ApiController, Authorize(Roles = "Coach"), Route("api/me/coach-profile")]
public sealed class OwnCoachProfileController(CoachProfileService service) : ControllerBase
{
    private int Actor => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpGet] public Task<CoachProfileResponse> Get(CancellationToken ct) => service.GetOwnAsync(Actor, ct);
    [HttpPut] public Task<CoachProfileResponse> Update(UpdateCoachProfileRequest body, CancellationToken ct) => service.UpdateOwnAsync(Actor, body, ct);
}
