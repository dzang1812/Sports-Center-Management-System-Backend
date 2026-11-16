using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Services.Membership;

namespace SportsCenter.API.Controllers;

[ApiController, Authorize(Roles = "Member,Receptionist"), Route("api/subscriptions")]
public sealed class SubscriptionsController(ISubscriptionService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    // Đăng ký gói qua thanh toán truyền thống.
    [HttpPost] public Task<PaymentDto> Subscribe(SubscribeRequest request, CancellationToken ct) => service.SubscribeAsync(ActorId, request, ct);
}

[ApiController]
public sealed class MemberSubscriptionsController(ISubscriptionService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // Xem gói đã mua, thời hạn và lượt tập còn lại.
    [HttpGet("api/me/subscriptions"), Authorize(Roles = "Member")]
    public Task<List<SubscriptionDto>> Mine(CancellationToken ct) => service.ListForMemberAsync(ActorId, null, ct);

    // Xem gói của hội viên được chọn.
    [HttpGet("api/members/{memberId:int}/subscriptions"), Authorize(Roles = "Receptionist,Manager")]
    public Task<List<SubscriptionDto>> ForMember(int memberId, CancellationToken ct) => service.ListForMemberAsync(ActorId, memberId, ct);
}

[ApiController, Authorize(Roles = "Member"), Route("api/me/subscriptions")]
public sealed class MemberSubscriptionCancellationController(SubscriptionCancellationService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // Xem trước điều kiện tự hủy gói không hoàn tiền.
    [HttpGet("{id:int}/cancellation")]
    public Task<SubscriptionCancellationPreview> Preview(int id, CancellationToken ct) => service.PreviewAsync(ActorId, id, ct);

    // Hội viên xác nhận ngừng sử dụng gói.
    [HttpPost("{id:int}/cancel")]
    public Task<SubscriptionCancellationResult> Cancel(int id, CancelSubscriptionRequest request, CancellationToken ct) =>
        service.CancelAsync(ActorId, id, request, ct);
}
