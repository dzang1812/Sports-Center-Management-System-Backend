using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PayOS.Models.Webhooks;
using SportsCenter.Services.Membership;

namespace SportsCenter.API.Controllers;

[ApiController, Route("api/payments")]
public sealed class PaymentsController(ISubscriptionService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // Xem trạng thái giao dịch và mã thanh toán.
    [HttpGet("{id:int}"), Authorize(Roles = "Member,Receptionist,Manager")]
    public Task<PaymentDto> Get(int id, CancellationToken ct) => service.GetPaymentAsync(ActorId, id, ct);

    // Đối chiếu giao dịch với cổng thanh toán.
    [HttpPost("{id:int}/sync-payos"), Authorize(Roles = "Member,Receptionist,Manager")]
    public Task<PaymentDto> SyncPayOs(int id, CancellationToken ct) => service.SyncPayOsStatusAsync(ActorId, id, ct);

    // Rà soát giao dịch trực tuyến đang chờ.
    [HttpPost("payos/sync-pending"), Authorize(Roles = "Manager")]
    public Task<PayOsSyncSummary> SyncPendingPayOs(CancellationToken ct) => service.SyncPendingPayOsAsync(ActorId, ct);

    // Xem các giao dịch thu tại quầy đang chờ xác nhận.
    [HttpGet("pending"), Authorize(Roles = "Receptionist")]
    public Task<List<PaymentDto>> Pending(CancellationToken ct) => service.ListPendingCounterPaymentsAsync(ActorId, ct);

    // Xác nhận đã thu tiền tại quầy.
    [HttpPost("{id:int}/confirm-at-counter"), Authorize(Roles = "Receptionist")]
    public Task<PaymentDto> ConfirmAtCounter(int id, CancellationToken ct) =>
        service.ConfirmCounterPaymentAsync(ActorId, id, ct);

    // Nhận xác nhận từ cổng thanh toán.
    [HttpPost("payos-webhook"), AllowAnonymous]
    public async Task<IActionResult> PayOsWebhook(Webhook webhook, CancellationToken ct)
    {
        await service.HandleWebhookAsync(webhook, ct);
        return Ok();
    }

    // Đăng ký địa chỉ nhận xác nhận thanh toán.
    [HttpPost("payos/confirm-webhook"), Authorize(Roles = "Manager")]
    public async Task<IActionResult> ConfirmWebhook([FromBody] string webhookUrl, CancellationToken ct) =>
        Ok(new { webhookUrl = await service.ConfirmWebhookUrlAsync(ActorId, webhookUrl, ct) });
}
