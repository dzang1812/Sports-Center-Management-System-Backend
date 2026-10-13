using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Services.Membership;

namespace SportsCenter.API.Controllers;

[ApiController, Authorize(Roles = "Member"), Route("api/me")]
public sealed class MyGymAccessController(IGymAccessService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("gym-card")]
    public Task<GymCardDto> Card(CancellationToken ct) => service.GetMyCardAsync(ActorId, ct);

    [HttpGet("gym-visits")]
    public Task<GymVisitHistoryResponse> Visits(
        DateOnly from, DateOnly to, int page = 1, int pageSize = 20, CancellationToken ct = default) =>
        service.ListVisitsAsync(ActorId, null, from, to, page, pageSize, ct);
}

[ApiController, Authorize(Roles = "Receptionist"), Route("api/gym-access")]
public sealed class GymAccessController(IGymAccessService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("cards/{cardCode}")]
    public Task<GymCardDto> Lookup(string cardCode, CancellationToken ct) =>
        service.LookupCardAsync(ActorId, cardCode, ct);

    [HttpPost("check-ins")]
    public Task<GymScanResult> CheckIn(GymScanRequest request, CancellationToken ct) =>
        service.CheckInAsync(ActorId, request, ct);

    [HttpPost("check-outs")]
    public Task<GymScanResult> CheckOut(GymScanRequest request, CancellationToken ct) =>
        service.CheckOutAsync(ActorId, request, ct);
}

[ApiController, Authorize(Roles = "Receptionist,Manager")]
public sealed class MemberGymVisitsController(IGymAccessService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("api/members/{memberId:int}/gym-visits")]
    public Task<GymVisitHistoryResponse> Visits(
        int memberId, DateOnly from, DateOnly to, int page = 1, int pageSize = 20, CancellationToken ct = default) =>
        service.ListVisitsAsync(ActorId, memberId, from, to, page, pageSize, ct);
}
