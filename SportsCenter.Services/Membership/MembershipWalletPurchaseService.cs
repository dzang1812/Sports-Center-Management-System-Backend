using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Payments;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.Services.Membership;

public record MembershipPurchaseResult(int SubscriptionId, int OrderId, decimal PaidAmount,
    DateOnly StartDate, DateOnly EndDate);

public sealed class MembershipWalletPurchaseService(IMembershipRepository plans, ICommerceRepository commerce,
    Flow2CommerceService purchases, WalletService wallet, ISchedulingTransaction transaction,
    IAuditLogRepository audit, IAppClock clock)
{
    public Task<MembershipPurchaseResult> BuyAsync(int actorId, int planId, BuyRequest request, string key,
        CancellationToken ct) => wallet.ExecuteAsync(actorId, $"membership-buy:{planId}", key, request, async token =>
    {
        var memberId = await wallet.TargetAsync(actorId, request.MemberId, true, token);
        await transaction.LockAsync([
            new(ScheduleResourceType.Member, memberId), new(ScheduleResourceType.MembershipPlan, planId),
        ], token);
        var plan = await plans.GetPlanAsync(planId, token)
            ?? throw SchedulingException.NotFound("PLAN_NOT_FOUND", "Không tìm thấy gói tập.");
        if (!plan.IsActive)
            throw SchedulingException.Conflict("PLAN_INACTIVE", "Gói tập đã ngừng bán.");
        if (!plan.IncludesGymAccess || plan.IncludesClasses || plan.PtSessionCredits != 0 || plan.WalletPtPackage)
            throw SchedulingException.Conflict("SELF_PRACTICE_PLAN_REQUIRED", "Chọn gói tự tập theo thời hạn; gói PT và lớp nhóm dùng luồng đăng ký riêng.");
        if (plan.SportId.HasValue && plan.Sport?.Status != SportStatus.Active)
            throw SchedulingException.Conflict("PLAN_SPORT_INACTIVE", "Bộ môn của gói đang ngừng hoạt động.");
        if ((plan.DurationDays > 0) == (plan.DurationMonths > 0) || plan.DurationDays is < 0 or > 3650 || plan.DurationMonths is < 0 or > 120)
            throw new SchedulingException("PLAN_DURATION_INVALID", "Thời hạn gói không hợp lệ.");
        Flow2CommerceService.EnsureExpectedPrice(request.ExpectedPrice, plan.Price);
        WalletService.ValidateMoney(plan.Price);
        if (await plans.HasPendingSubscriptionPaymentAsync(memberId, planId, token))
            throw SchedulingException.Conflict("SUBSCRIPTION_PAYMENT_PENDING", "Gói này đang có giao dịch chờ thanh toán. Vui lòng xử lý giao dịch đó trước khi mua bằng số dư.");

        var today = DateOnly.FromDateTime(clock.LocalNow);
        var latest = await plans.GetLatestSubscriptionAsync(memberId, planId, token);
        DateOnly start, end;
        try
        {
            start = latest is not null && latest.EndDate >= today ? latest.EndDate.AddDays(1) : today;
            end = plan.DurationMonths > 0 ? start.AddMonths(plan.DurationMonths).AddDays(-1) : start.AddDays(plan.DurationDays - 1);
        }
        catch (ArgumentOutOfRangeException) { throw new SchedulingException("PLAN_DURATION_INVALID", "Thời hạn gia hạn vượt giới hạn ngày hỗ trợ."); }
        var order = await purchases.PurchaseAsync(memberId, actorId, CommerceOrderKind.Membership, plan.Price,
            new { plan.Id, plan.Name, plan.Price, plan.SportId, plan.DurationDays, plan.DurationMonths,
                plan.IncludesGymAccess, StartDate = start, EndDate = end, Policy = "self-practice-wallet-v1" }, token);
        var subscription = new MemberSubscription
        {
            MemberId = memberId, PlanId = planId, PurchaseOrderId = order.Id,
            SnapshotSportId = plan.SportId, SnapshotPtCredits = 0,
            StartDate = start, EndDate = end, Status = SubscriptionStatus.Active,
            IncludesGymAccess = true, RemainingPtSessions = 0,
        };
        commerce.Add(subscription);
        await transaction.SaveChangesAsync(token);
        audit.AddAudit(actorId, "Membership.WalletPurchase", nameof(MemberSubscription), subscription.Id,
            new { memberId, planId, order.Id, start, end });
        return new MembershipPurchaseResult(subscription.Id, order.Id, order.PaidAmount, start, end);
    }, ct);
}
