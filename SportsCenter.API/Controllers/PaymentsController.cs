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

    [HttpGet("{id:int}"), Authorize(Roles = "Member,Receptionist,Manager")]
    public Task<PaymentDto> Get(int id, CancellationToken ct) => service.GetPaymentAsync(ActorId, id, ct);

    // Called by the PayOS cancel page: asks PayOS for the link status and closes the payment if it was cancelled/expired.
    [HttpPost("{id:int}/sync-payos"), Authorize(Roles = "Member,Receptionist,Manager")]
    public Task<PaymentDto> SyncPayOs(int id, CancellationToken ct) => service.SyncPayOsStatusAsync(ActorId, id, ct);

    // Manager clean-up: re-checks every pending PayOS payment, cancelling links older than their 15-minute lifetime.
    [HttpPost("payos/sync-pending"), Authorize(Roles = "Manager")]
    public Task<PayOsSyncSummary> SyncPendingPayOs(CancellationToken ct) => service.SyncPendingPayOsAsync(ActorId, ct);

    // Cash/Card orders started at the desk, still waiting for the receptionist to confirm cash-in-hand.
    [HttpGet("pending"), Authorize(Roles = "Receptionist")]
    public Task<List<PaymentDto>> Pending(CancellationToken ct) => service.ListPendingCounterPaymentsAsync(ActorId, ct);

    [HttpPost("{id:int}/confirm-at-counter"), Authorize(Roles = "Receptionist")]
    public Task<PaymentDto> ConfirmAtCounter(int id, CancellationToken ct) =>
        service.ConfirmCounterPaymentAsync(ActorId, id, ct);

    // PayOS calls this directly (no JWT); Webhooks.VerifyAsync inside the service checks the signature.
    [HttpPost("payos-webhook"), AllowAnonymous]
    public async Task<IActionResult> PayOsWebhook(Webhook webhook, CancellationToken ct)
    {
        await service.HandleWebhookAsync(webhook, ct);
        return Ok();
    }

    // One-off: run after deploying, to register the real public URL with PayOS (dashboard shows an error
    // "Chưa cấu hình webhook" until this succeeds once). Not callable against a localhost URL.
    [HttpPost("payos/confirm-webhook"), Authorize(Roles = "Manager")]
    public async Task<IActionResult> ConfirmWebhook([FromBody] string webhookUrl, CancellationToken ct) =>
        Ok(new { webhookUrl = await service.ConfirmWebhookUrlAsync(ActorId, webhookUrl, ct) });
}
