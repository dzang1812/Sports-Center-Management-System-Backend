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

[ApiController, Route("api")]
public sealed class CoachReviewsController(CoachReviewService service) : ControllerBase
{
    private int Actor => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpGet("coaches/ratings"), AllowAnonymous]
    public Task<List<CoachRatingDto>> Ratings(CancellationToken ct) => service.ListRatingsAsync(ct);
    [HttpGet("coaches/{coachId:int}/reviews"), AllowAnonymous]
    public Task<CoachReviewsResponse> Reviews(int coachId, CancellationToken ct) => service.ListForCoachAsync(coachId, ct);
    [HttpGet("me/coach-reviews"), Authorize(Roles = "Member")]
    public Task<List<ReviewableCoachDto>> Mine(CancellationToken ct) => service.ListMineAsync(Actor, ct);
    [HttpPut("me/coach-reviews/{coachId:int}"), Authorize(Roles = "Member")]
    public Task<ReviewableCoachDto> Upsert(int coachId, UpsertCoachReviewRequest body, CancellationToken ct) => service.UpsertAsync(Actor, coachId, body, ct);
    [HttpGet("coach-reviews"), Authorize(Roles = "Manager,Receptionist")]
    public Task<List<AdminCoachReviewItem>> All([FromQuery] int? coachId, CancellationToken ct) => service.ListAllAsync(Actor, coachId, ct);
    [HttpDelete("coach-reviews/{id:int}"), Authorize(Roles = "Manager,Receptionist")]
    public async Task<IActionResult> Delete(int id, [FromQuery] string? reason, CancellationToken ct) { await service.DeleteAsync(Actor, id, reason, ct); return NoContent(); }
    [HttpPut("coaches/{coachId:int}/featured"), Authorize(Roles = "Manager,Receptionist")]
    public Task<CoachRatingDto> Feature(int coachId, SetCoachFeaturedRequest body, CancellationToken ct) => service.SetFeaturedAsync(Actor, coachId, body.Featured, ct);
}

[ApiController, Authorize(Roles = "Coach"), Route("api/me/coach-profile")]
public sealed class OwnCoachProfileController(CoachProfileService service) : ControllerBase
{
    private int Actor => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpGet] public Task<CoachProfileResponse> Get(CancellationToken ct) => service.GetOwnAsync(Actor, ct);
    [HttpPut] public Task<CoachProfileResponse> Update(UpdateCoachProfileRequest body, CancellationToken ct) => service.UpdateOwnAsync(Actor, body, ct);
}
