using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Services.Membership;

namespace SportsCenter.API.Controllers;

[ApiController, Route("api/membership-plans")]
public sealed class MembershipPlansController(IMembershipPlanService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet, AllowAnonymous] public Task<List<PlanDto>> List(CancellationToken ct) => service.ListAsync(activeOnly: true, ct);
    [HttpGet("{id:int}"), AllowAnonymous]
    public Task<PlanDto> Get(int id, CancellationToken ct) => service.GetAsync(id, User.IsInRole("Manager"), ct);
    [HttpGet("all"), Authorize(Roles = "Manager")] public Task<List<PlanDto>> ListAll(CancellationToken ct) => service.ListAsync(activeOnly: false, ct);
    [HttpPost, Authorize(Roles = "Manager")] public Task<PlanDto> Create(UpsertPlanRequest request, CancellationToken ct) => service.CreateAsync(ActorId, request, ct);
    [HttpPut("{id:int}"), Authorize(Roles = "Manager")] public Task<PlanDto> Update(int id, UpsertPlanRequest request, CancellationToken ct) => service.UpdateAsync(ActorId, id, request, ct);
}
