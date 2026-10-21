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

public record SubscribeRequest([param: Required, Range(1, int.MaxValue)] int PlanId, int? MemberId,
    PaymentMethod? Method = null);

public record SubscriptionDto(int Id, int MemberId, int PlanId, string PlanName, DateOnly StartDate, DateOnly EndDate,
    SubscriptionStatus Status, int RemainingPtSessions, bool IncludesGymAccess, string PlanType, int TotalPtSessions,
    int RemainingDays, bool IncludesClasses, int? SportId, string? SportName,
    IReadOnlyList<PlanSportDto> AllowedSports, PaymentStatus? PaymentStatus);

// Covers both pending checkout and settled invoice; clients branch on Status.
public record PaymentDto(int Id, int MemberId, string MemberName, int? SubscriptionId, string? PlanName,
    decimal Amount, PaymentMethod Method, PaymentStatus Status, string? InvoiceNumber, DateTime PaidAt,
    string? CheckoutUrl, string? QrCode, string? ReceivedByName);

public interface ISubscriptionService
{
    Task<PaymentDto> SubscribeAsync(int actorId, SubscribeRequest request, CancellationToken ct);
    Task<List<SubscriptionDto>> ListForMemberAsync(int actorId, int? memberId, CancellationToken ct);
    Task<PaymentDto> GetPaymentAsync(int actorId, int paymentId, CancellationToken ct);
    Task<PaymentDto> ConfirmCounterPaymentAsync(int actorId, int paymentId, CancellationToken ct);
    Task<List<PaymentDto>> ListPendingCounterPaymentsAsync(int actorId, CancellationToken ct);
    Task HandleWebhookAsync(Webhook webhook, CancellationToken ct);
    Task<string> ConfirmWebhookUrlAsync(int actorId, string webhookUrl, CancellationToken ct);
}

public sealed class SubscriptionService(IMembershipRepository repo, IUserRepository users, IAppClock clock,
    ISchedulingTransaction transaction, PayOSClient payOs, IOptions<MailOptions> mailOptions) : ISubscriptionService
{
    public async Task<PaymentDto> SubscribeAsync(int actorId, SubscribeRequest request, CancellationToken ct)
    {
        var (actor, member) = await ResolveSubscribeTargetAsync(actorId, request.MemberId);
        var method = request.Method ?? PaymentMethod.BankTransfer;
        if (!Enum.IsDefined(method))
            throw new SchedulingException("PAYMENT_METHOD_INVALID", "Phương thức thanh toán không hợp lệ.");
        if (method != PaymentMethod.BankTransfer && actor.Role != UserRole.Receptionist)
            throw SchedulingException.Forbidden("COUNTER_PAYMENT_REQUIRES_RECEPTIONIST",
                "Chỉ lễ tân được tạo thanh toán tiền mặt hoặc thẻ tại quầy.");

        return await transaction.ExecuteAsync(async token =>
        {
            await transaction.LockAsync([new(ScheduleResourceType.Member, member.Id)], token);
            var plan = await repo.GetPlanAsync(request.PlanId, token)
                ?? throw SchedulingException.NotFound("PLAN_NOT_FOUND", "Không tìm thấy gói thành viên.");
            if (!plan.IsActive)
                throw SchedulingException.Conflict("PLAN_INACTIVE", "Gói thành viên đã ngừng bán.");
            if (plan.SportId.HasValue && plan.Sport?.Status != SportStatus.Active &&
                (plan.IncludesClasses || plan.PtSessionCredits > 0))
                throw SchedulingException.Conflict("PLAN_SPORT_INACTIVE", "Bộ môn của gói đã ngừng hoạt động.");
            if (await repo.HasPendingSubscriptionPaymentAsync(member.Id, plan.Id, token))
                throw SchedulingException.Conflict("SUBSCRIPTION_PAYMENT_PENDING",
                    "Đã có đơn thanh toán đang chờ cho gói này.");

            var today = DateOnly.FromDateTime(clock.LocalNow);
            var latest = await repo.GetLatestSubscriptionAsync(member.Id, plan.Id, token);
            var startDate = latest is not null && latest.EndDate >= today ? latest.EndDate.AddDays(1) : today;
            var subscription = new MemberSubscription
            {
                MemberId = member.Id,
                PlanId = plan.Id,
                StartDate = startDate,
                EndDate = CalculateEndDate(startDate, plan),
                Status = SubscriptionStatus.Pending,
                RemainingPtSessions = plan.PtSessionCredits,
                IncludesGymAccess = plan.IncludesGymAccess,
            };
            await repo.AddSubscriptionAsync(subscription, token);
            await transaction.SaveChangesAsync(token);

            var payment = new Payment
            {
                MemberId = member.Id,
                SubscriptionId = subscription.Id,
                ReceivedById = actor.Role == UserRole.Receptionist ? actorId : null,
                Amount = plan.Price,
                Method = method,
            };
            await repo.AddPaymentAsync(payment, token);
            await transaction.SaveChangesAsync(token);

            payment.PayOsOrderCode = payment.Id;
            if (method == PaymentMethod.BankTransfer)
            {
                var link = await CreatePayOsLinkAsync(payment, token);
                payment.PayOsPaymentLinkId = link.PaymentLinkId;
                payment.CheckoutUrl = link.CheckoutUrl;
                payment.QrCode = link.QrCode;
            }

            await repo.AddAuditAsync(actorId, "Subscription.Create", nameof(MemberSubscription), subscription.Id,
                new { memberId = member.Id, planId = plan.Id, subscription.StartDate, subscription.EndDate, payment.Amount }, token);
            return ToPaymentDto(payment, member, plan.Name);
        }, ct);
    }

    public async Task<PaymentDto> ConfirmCounterPaymentAsync(int actorId, int paymentId, CancellationToken ct)
    {
        await MembershipRules.EnsureActiveRoleAsync(users, actorId, UserRole.Receptionist);
        var key = await repo.GetPaymentKeyAsync(paymentId, ct)
            ?? throw SchedulingException.NotFound("PAYMENT_NOT_FOUND", "Không tìm thấy giao dịch thanh toán.");

        return await transaction.ExecuteAsync(async token =>
        {
            await transaction.LockAsync([new(ScheduleResourceType.Member, key.MemberId)], token);
            var payment = await repo.GetPaymentWithDetailsAsync(paymentId, token)
                ?? throw SchedulingException.NotFound("PAYMENT_NOT_FOUND", "Không tìm thấy giao dịch thanh toán.");
            if (payment.Method == PaymentMethod.BankTransfer)
                throw SchedulingException.Conflict("PAYMENT_REQUIRES_GATEWAY",
                    "Thanh toán chuyển khoản chỉ được xác nhận qua PayOS.");
            if (payment.Status == PaymentStatus.Paid)
                return ToPaymentDto(payment, payment.Member, payment.Subscription?.Plan.Name);
            if (payment.Status != PaymentStatus.Pending)
                throw SchedulingException.Conflict("PAYMENT_NOT_PENDING",
                    "Giao dịch không còn ở trạng thái chờ xác nhận.");

            await ConfirmPaymentAsync(payment, actorId, $"counter:{actorId}", token);
            await transaction.SaveChangesAsync(token);
            return ToPaymentDto(payment, payment.Member, payment.Subscription?.Plan.Name);
        }, ct);
    }

    public async Task<List<PaymentDto>> ListPendingCounterPaymentsAsync(int actorId, CancellationToken ct)
    {
        await MembershipRules.EnsureActiveRoleAsync(users, actorId, UserRole.Receptionist);
        var payments = await repo.ListPendingCounterPaymentsAsync(ct);
        return payments.Select(p => ToPaymentDto(p, p.Member, p.Subscription?.Plan.Name)).ToList();
    }

    public async Task<List<SubscriptionDto>> ListForMemberAsync(int actorId, int? memberId, CancellationToken ct)
    {
        var targetId = await ResolveViewTargetAsync(actorId, memberId);
        var subscriptions = await repo.ListSubscriptionsAsync(targetId, ct);
        var paymentStatuses = await repo.GetLatestPaymentStatusesAsync(subscriptions.Select(x => x.Id).ToList(), ct);
        var sports = await repo.ListSportsAsync(ct);
        var today = DateOnly.FromDateTime(clock.LocalNow);
        return subscriptions.Select(x => ToSubscriptionDto(x, sports, today,
            paymentStatuses.TryGetValue(x.Id, out var status) ? status : null)).ToList();
    }

    public async Task<PaymentDto> GetPaymentAsync(int actorId, int paymentId, CancellationToken ct)
    {
        var actor = await users.GetByIdAsync(actorId)
            ?? throw SchedulingException.Forbidden("ACTOR_INACTIVE", "Tài khoản không hợp lệ hoặc đã bị khóa.");
        var payment = await repo.GetPaymentWithDetailsAsync(paymentId, ct)
            ?? throw SchedulingException.NotFound("PAYMENT_NOT_FOUND", "Không tìm thấy giao dịch thanh toán.");
        if (actor.Role == UserRole.Member && payment.MemberId != actorId)
            throw SchedulingException.Forbidden("PAYMENT_NOT_OWNED", "Không thể xem giao dịch của hội viên khác.");
        if (actor.Role is not (UserRole.Member or UserRole.Receptionist or UserRole.Manager))
            throw SchedulingException.Forbidden("ROLE_NOT_ALLOWED", "Vai trò không được xem giao dịch thanh toán.");
        return ToPaymentDto(payment, payment.Member, payment.Subscription?.Plan.Name);
    }

    // PayOS calls this unauthenticated; VerifyAsync authenticates the webhook signature.
    public async Task HandleWebhookAsync(Webhook webhook, CancellationToken ct)
    {
        WebhookData data;
        try { data = await payOs.Webhooks.VerifyAsync(webhook); }
        catch (Exception) { throw new SchedulingException("PAYOS_INVALID_SIGNATURE", "Chữ ký webhook không hợp lệ."); }

        var key = await repo.GetPaymentKeyByOrderCodeAsync(data.OrderCode, ct);
        if (key is null) return;
        await transaction.ExecuteAsync(async token =>
        {
            await transaction.LockAsync([new(ScheduleResourceType.Member, key.MemberId)], token);
            var payment = await repo.GetPaymentByOrderCodeAsync(data.OrderCode, token);
            if (payment is null || payment.Status != PaymentStatus.Pending) return 0;

            if (webhook.Success)
                await ConfirmPaymentAsync(payment, payment.MemberId, data.Reference, token);
            else
            {
                payment.Status = PaymentStatus.Failed;
                if (payment.Subscription is not null) payment.Subscription.Status = SubscriptionStatus.Cancelled;
                await repo.AddAuditAsync(payment.MemberId, "Payment.Failed", nameof(Payment), payment.Id,
                    new { payment.PayOsOrderCode }, token);
            }
            return 0;
        }, ct);
    }

    public async Task<string> ConfirmWebhookUrlAsync(int actorId, string webhookUrl, CancellationToken ct)
    {
        await MembershipRules.EnsureActiveRoleAsync(users, actorId, UserRole.Manager);
        var result = await payOs.Webhooks.ConfirmAsync(webhookUrl);
        return result.WebhookUrl;
    }

    private async Task<CreatePaymentLinkResponse> CreatePayOsLinkAsync(Payment payment, CancellationToken ct)
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
        catch (Exception)
        {
            throw new SchedulingException("PAYOS_LINK_FAILED",
                "Không tạo được link thanh toán PayOS. Vui lòng thử lại.", 502);
        }
    }

    private async Task ConfirmPaymentAsync(Payment payment, int actorId, string reference, CancellationToken ct)
    {
        payment.Status = PaymentStatus.Paid;
        payment.PaidAt = clock.UtcNow;
        payment.InvoiceNumber = $"INV-{clock.LocalNow:yyyyMMdd}-{payment.SubscriptionId:D6}";
        if (payment.Subscription is not null) payment.Subscription.Status = SubscriptionStatus.Active;
        await repo.AddAuditAsync(actorId, "Payment.Confirmed", nameof(Payment), payment.Id,
            new { payment.PayOsOrderCode, Reference = reference }, ct);
    }

    private static DateOnly CalculateEndDate(DateOnly startDate, MembershipPlan plan) =>
        plan.DurationMonths > 0
            ? startDate.AddMonths(plan.DurationMonths).AddDays(-1)
            : startDate.AddDays(plan.DurationDays - 1);

    private async Task<(User Actor, User Member)> ResolveSubscribeTargetAsync(int actorId, int? requestedMemberId)
    {
        var actor = await users.GetByIdAsync(actorId)
            ?? throw SchedulingException.Forbidden("ACTOR_INACTIVE", "Tài khoản không hợp lệ hoặc đã bị khóa.");
        var targetId = actor.Role switch
        {
            UserRole.Member when requestedMemberId is null || requestedMemberId == actorId => actorId,
            UserRole.Member => throw SchedulingException.Forbidden("CANNOT_ACT_FOR_MEMBER",
                "Hội viên không thể đăng ký hộ người khác."),
            UserRole.Receptionist when requestedMemberId is not null => requestedMemberId.Value,
            UserRole.Receptionist => throw new SchedulingException("TARGET_MEMBER_REQUIRED", "Lễ tân phải chọn hội viên."),
            _ => throw SchedulingException.Forbidden("ROLE_NOT_ALLOWED", "Vai trò không được đăng ký gói."),
        };
        var member = targetId == actorId ? actor : await users.GetByIdAsync(targetId);
        if (member is null || member.Role != UserRole.Member)
            throw SchedulingException.NotFound("MEMBER_NOT_FOUND", "Không tìm thấy hội viên đang hoạt động.");
        return (actor, member);
    }

    private async Task<int> ResolveViewTargetAsync(int actorId, int? requestedMemberId)
    {
        var actor = await users.GetByIdAsync(actorId)
            ?? throw SchedulingException.Forbidden("ACTOR_INACTIVE", "Tài khoản không hợp lệ hoặc đã bị khóa.");
        int targetId;
        if (actor.Role == UserRole.Member)
        {
            if (requestedMemberId is not null && requestedMemberId != actorId)
                throw SchedulingException.Forbidden("SUBSCRIPTIONS_NOT_OWNED", "Không thể xem gói của hội viên khác.");
            targetId = actorId;
        }
        else if (actor.Role is UserRole.Receptionist or UserRole.Manager)
        {
            targetId = requestedMemberId
                ?? throw new SchedulingException("TARGET_MEMBER_REQUIRED", "Thiếu memberId.");
        }
        else
        {
            throw SchedulingException.Forbidden("ROLE_NOT_ALLOWED", "Vai trò không được xem gói hội viên.");
        }

        var target = targetId == actorId ? actor : await users.GetByIdAsync(targetId);
        if (target is null || target.Role != UserRole.Member)
            throw SchedulingException.NotFound("MEMBER_NOT_FOUND", "Không tìm thấy hội viên đang hoạt động.");
        return targetId;
    }

    private static SubscriptionDto ToSubscriptionDto(MemberSubscription subscription,
        IReadOnlyCollection<Sport> sports, DateOnly today, PaymentStatus? paymentStatus)
    {
        var status = subscription.Status == SubscriptionStatus.Active && subscription.EndDate < today
            ? SubscriptionStatus.Expired
            : subscription.Status;
        var remainingDays = status is SubscriptionStatus.Cancelled or SubscriptionStatus.Expired
            ? 0
            : Math.Max(0, subscription.EndDate.DayNumber - Math.Max(today.DayNumber, subscription.StartDate.DayNumber) + 1);
        var allowedSports = MembershipPlanPresentation.GetAllowedSports(subscription.Plan, sports)
            .Select(x => new PlanSportDto(x.Id, x.Name)).ToList();
        return new SubscriptionDto(subscription.Id, subscription.MemberId, subscription.PlanId, subscription.Plan.Name,
            subscription.StartDate, subscription.EndDate, status, subscription.RemainingPtSessions,
            subscription.IncludesGymAccess, MembershipPlanPresentation.GetPlanType(subscription.Plan),
            subscription.Plan.PtSessionCredits, remainingDays, subscription.Plan.IncludesClasses,
            subscription.Plan.SportId, subscription.Plan.Sport?.Name, allowedSports, paymentStatus);
    }

    private static PaymentDto ToPaymentDto(Payment payment, User member, string? planName) =>
        new(payment.Id, payment.MemberId, member.FullName, payment.SubscriptionId, planName, payment.Amount,
            payment.Method, payment.Status, payment.InvoiceNumber, payment.PaidAt, payment.CheckoutUrl,
            payment.QrCode, payment.ReceivedBy?.FullName);
}
