using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.API.Controllers;

[ApiController, Authorize(Roles = "Coach"), Route("api/coach/teaching-summary")]
public sealed class OwnCoachTeachingController(CoachTeachingService service) : ControllerBase
{
    [HttpGet]
    public Task<CoachTeachingReport> Get(int year, int quarter, CancellationToken ct) =>
        service.GetOwnAsync(int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), year, quarter, ct);
}

[ApiController, Authorize(Roles = "Manager"), Route("api/reports/coach-teaching")]
public sealed class CoachTeachingController(CoachTeachingService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpGet]
    public Task<CoachTeachingOverview> Get(int year, int quarter, CancellationToken ct) =>
        service.GetOverviewAsync(ActorId, year, quarter, ct);

    [HttpGet("{coachId:int}")]
    public Task<CoachTeachingReport> Coach(int coachId, int year, int quarter, CancellationToken ct) =>
        service.GetCoachAsync(ActorId, coachId, year, quarter, ct);
}
