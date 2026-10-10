using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using PayOS;
using PayOS.Models.V2.PaymentRequests;
using PayOS.Models.Webhooks;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Email;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.Services.Membership;

public record SubscribeRequest([param: Required] int PlanId, int? MemberId);

public record SubscriptionDto(int Id, int MemberId, int PlanId, string PlanName, DateOnly StartDate, DateOnly EndDate,
    SubscriptionStatus Status, int RemainingPtSessions);

// Covers both "pending checkout" (right after Subscribe, or re-fetched later to re-show the QR) and
// "settled invoice" (once Status = Paid) — one shape, FE branches on Status.
public record PaymentDto(int Id, int MemberId, string MemberName, int? SubscriptionId, string? PlanName,
    decimal Amount, PaymentMethod Method, PaymentStatus Status, string? InvoiceNumber, DateTime PaidAt,
    string? CheckoutUrl, string? QrCode, string? ReceivedByName);

public interface ISubscriptionService
{
    Task<PaymentDto> SubscribeAsync(int actorId, SubscribeRequest request, CancellationToken ct);
    Task<List<SubscriptionDto>> ListForMemberAsync(int actorId, int? memberId, CancellationToken ct);
    Task<PaymentDto> GetPaymentAsync(int actorId, int paymentId, CancellationToken ct);
    Task HandleWebhookAsync(Webhook webhook, CancellationToken ct);
    Task<string> ConfirmWebhookUrlAsync(int actorId, string webhookUrl, CancellationToken ct);
}

// ponytail: no advisory lock on (member, plan) during Subscribe, unlike ClassBookingService.
// A double-submit within milliseconds could create 2 Pending subscriptions for the same plan.
// Harmless (the stale one just never gets paid), add ISchedulingTransaction.LockAsync(Member) if it
// turns out to confuse members in practice.
public sealed class SubscriptionService(IMembershipRepository repo, IUserRepository users, IAppClock clock,
    PayOSClient payOs, IOptions<MailOptions> mailOptions) : ISubscriptionService
{
    public async Task<PaymentDto> SubscribeAsync(int actorId, SubscribeRequest request, CancellationToken ct)
    {
        var (actor, memberId) = await ResolveSubscribeTargetAsync(actorId, request.MemberId, ct);
        var plan = await repo.GetPlanAsync(request.PlanId, ct)
            ?? throw SchedulingException.NotFound("PLAN_NOT_FOUND", "Không tìm thấy gói thành viên.");
        if (!plan.IsActive) throw SchedulingException.Conflict("PLAN_INACTIVE", "Gói thành viên đã ngừng bán.");

        var today = DateOnly.FromDateTime(clock.LocalNow);
        var latest = await repo.GetLatestSubscriptionAsync(memberId, plan.Id, ct);
        var startDate = latest is not null && latest.Status != SubscriptionStatus.Cancelled && latest.EndDate >= today
            ? latest.EndDate.AddDays(1) : today;
        var endDate = startDate.AddDays(plan.DurationDays - 1);

        var subscription = new MemberSubscription
        {
            MemberId = memberId, PlanId = plan.Id, StartDate = startDate, EndDate = endDate,
            Status = SubscriptionStatus.Pending, RemainingPtSessions = plan.PtSessionCredits,
        };
        await repo.AddSubscriptionAsync(subscription, ct);
        await repo.SaveChangesAsync(ct); // assigns subscription.Id

        var payment = new Payment
        {
            MemberId = memberId, SubscriptionId = subscription.Id,
            ReceivedById = actor.Role == UserRole.Receptionist ? actorId : null,
            Amount = plan.Price,
        };
        await repo.AddPaymentAsync(payment, ct);
        await repo.SaveChangesAsync(ct); // assigns payment.Id, used as the PayOS orderCode

        payment.PayOsOrderCode = payment.Id;
        var link = await CreatePayOsLinkAsync(payment, plan.Name, ct);
        payment.PayOsPaymentLinkId = link.PaymentLinkId;
        payment.CheckoutUrl = link.CheckoutUrl;
        payment.QrCode = link.QrCode;

        await repo.AddAuditAsync(actorId, "Subscription.Create", nameof(MemberSubscription), subscription.Id,
            new { memberId, planId = plan.Id, subscription.StartDate, subscription.EndDate, payment.Amount }, ct);
        await repo.SaveChangesAsync(ct);

        return ToPaymentDto(payment, actor, plan.Name);
    }

    public async Task<List<SubscriptionDto>> ListForMemberAsync(int actorId, int? memberId, CancellationToken ct)
    {
        var targetId = await ResolveViewTargetAsync(actorId, memberId);
        var subscriptions = await repo.ListSubscriptionsAsync(targetId, ct);
        return subscriptions.Select(s => ToSubscriptionDto(s, s.Plan.Name)).ToList();
    }

    public async Task<PaymentDto> GetPaymentAsync(int actorId, int paymentId, CancellationToken ct)
    {
        var actor = await users.GetByIdAsync(actorId) ?? throw SchedulingException.Forbidden("ACTOR_INACTIVE", "Tài khoản không hợp lệ hoặc đã bị khóa.");
        var payment = await repo.GetPaymentWithDetailsAsync(paymentId, ct)
            ?? throw SchedulingException.NotFound("PAYMENT_NOT_FOUND", "Không tìm thấy giao dịch thanh toán.");
        if (actor.Role == UserRole.Member && payment.MemberId != actorId)
            throw SchedulingException.Forbidden("PAYMENT_NOT_OWNED", "Không thể xem giao dịch của hội viên khác.");

        return ToPaymentDto(payment, payment.Member, payment.Subscription?.Plan.Name);
    }

    // PayOS calls this unauthenticated; the signature check inside VerifyAsync IS the authentication.
    // Also hit once with a dummy payload when ConfirmWebhookUrlAsync registers the URL — an unknown
    // orderCode or an already-settled payment is acked with no-op, not an error.
    public async Task HandleWebhookAsync(Webhook webhook, CancellationToken ct)
    {
        WebhookData data;
        try { data = await payOs.Webhooks.VerifyAsync(webhook); }
        catch (Exception) { throw new SchedulingException("PAYOS_INVALID_SIGNATURE", "Chữ ký webhook không hợp lệ.", 400); }

        var payment = await repo.GetPaymentByOrderCodeAsync(data.OrderCode, ct);
        if (payment is null || payment.Status != PaymentStatus.Pending) return;

        if (webhook.Success)
        {
            payment.Status = PaymentStatus.Paid;
            payment.PaidAt = clock.UtcNow;
            payment.InvoiceNumber = $"INV-{clock.LocalNow:yyyyMMdd}-{payment.SubscriptionId:D6}";
            if (payment.Subscription is not null) payment.Subscription.Status = SubscriptionStatus.Active;
            await repo.AddAuditAsync(payment.MemberId, "Payment.Confirmed", nameof(Payment), payment.Id,
                new { payment.PayOsOrderCode, data.Reference }, ct);
        }
        else
        {
            payment.Status = PaymentStatus.Failed;
            if (payment.Subscription is not null) payment.Subscription.Status = SubscriptionStatus.Cancelled;
            await repo.AddAuditAsync(payment.MemberId, "Payment.Failed", nameof(Payment), payment.Id,
                new { payment.PayOsOrderCode }, ct);
        }
        await repo.SaveChangesAsync(ct);
    }

    public async Task<string> ConfirmWebhookUrlAsync(int actorId, string webhookUrl, CancellationToken ct)
    {
        await MembershipRules.EnsureActiveRoleAsync(users, actorId, UserRole.Manager);
        var result = await payOs.Webhooks.ConfirmAsync(webhookUrl);
        return result.WebhookUrl;
    }

    private async Task<CreatePaymentLinkResponse> CreatePayOsLinkAsync(Payment payment, string planName, CancellationToken ct)
    {
        var baseUrl = mailOptions.Value.FrontendUrl.TrimEnd('/');
        var description = $"Thanh toan {payment.Id}";
        var request = new CreatePaymentLinkRequest
        {
            OrderCode = payment.PayOsOrderCode,
            Amount = (long)payment.Amount,
            Description = description.Length > 25 ? description[..25] : description,
            ReturnUrl = $"{baseUrl}/payment/success?paymentId={payment.Id}",
            CancelUrl = $"{baseUrl}/payment/cancel?paymentId={payment.Id}",
        };
        try { return await payOs.PaymentRequests.CreateAsync(request); }
        catch (Exception ex) { throw new SchedulingException("PAYOS_LINK_FAILED", "Không tạo được link thanh toán PayOS. Vui lòng thử lại.", 502, new { ex.Message }); }
    }

    private async Task<(User Actor, int MemberId)> ResolveSubscribeTargetAsync(int actorId, int? requestedMemberId, CancellationToken ct)
    {
        var actor = await users.GetByIdAsync(actorId) ?? throw SchedulingException.Forbidden("ACTOR_INACTIVE", "Tài khoản không hợp lệ hoặc đã bị khóa.");
        var targetId = actor.Role switch
        {
            UserRole.Member when requestedMemberId is null || requestedMemberId == actorId => actorId,
            UserRole.Member => throw SchedulingException.Forbidden("CANNOT_ACT_FOR_MEMBER", "Hội viên không thể đăng ký hộ người khác."),
            UserRole.Receptionist when requestedMemberId is not null => requestedMemberId.Value,
            UserRole.Receptionist => throw new SchedulingException("TARGET_MEMBER_REQUIRED", "Lễ tân phải chọn hội viên."),
            _ => throw SchedulingException.Forbidden("ROLE_NOT_ALLOWED", "Vai trò không được đăng ký gói."),
        };
        var target = targetId == actorId ? actor : await users.GetByIdAsync(targetId);
        if (target is null || target.Role != UserRole.Member)
            throw SchedulingException.NotFound("MEMBER_NOT_FOUND", "Không tìm thấy hội viên đang hoạt động.");
        return (actor, targetId);
    }

    private async Task<int> ResolveViewTargetAsync(int actorId, int? requestedMemberId)
    {
        var actor = await users.GetByIdAsync(actorId) ?? throw SchedulingException.Forbidden("ACTOR_INACTIVE", "Tài khoản không hợp lệ hoặc đã bị khóa.");
        if (actor.Role == UserRole.Member) return actorId;
        return requestedMemberId ?? throw new SchedulingException("TARGET_MEMBER_REQUIRED", "Thiếu memberId.");
    }

    private static SubscriptionDto ToSubscriptionDto(MemberSubscription x, string planName) =>
        new(x.Id, x.MemberId, x.PlanId, planName, x.StartDate, x.EndDate, x.Status, x.RemainingPtSessions);

    private static PaymentDto ToPaymentDto(Payment x, User member, string? planName) =>
        new(x.Id, x.MemberId, member.FullName, x.SubscriptionId, planName, x.Amount, x.Method, x.Status,
            x.InvoiceNumber, x.PaidAt, x.CheckoutUrl, x.QrCode, x.ReceivedBy?.FullName);
}
