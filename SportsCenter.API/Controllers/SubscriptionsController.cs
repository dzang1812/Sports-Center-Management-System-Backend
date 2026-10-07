using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Services.Membership;

namespace SportsCenter.API.Controllers;

[ApiController, Authorize(Roles = "Member,Receptionist"), Route("api/subscriptions")]
public sealed class SubscriptionsController(ISubscriptionService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpPost] public Task<PaymentDto> Subscribe(SubscribeRequest request, CancellationToken ct) => service.SubscribeAsync(ActorId, request, ct);
}

[ApiController]
public sealed class MemberSubscriptionsController(ISubscriptionService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("api/me/subscriptions"), Authorize]
    public Task<List<SubscriptionDto>> Mine(CancellationToken ct) => service.ListForMemberAsync(ActorId, null, ct);

    [HttpGet("api/members/{memberId:int}/subscriptions"), Authorize(Roles = "Receptionist,Manager")]
    public Task<List<SubscriptionDto>> ForMember(int memberId, CancellationToken ct) => service.ListForMemberAsync(ActorId, memberId, ct);
}
