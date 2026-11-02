using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.Services.Membership;

public record CancelSubscriptionRequest(bool Confirmed, decimal? ExpectedRefundAmount, string? PolicyVersion);
public record SubscriptionCancellationPreview(int SubscriptionId, string PlanName, bool CanCancel,
    string? UnavailableReason, decimal RefundAmount, string PolicyVersion, string Policy);
public record SubscriptionCancellationResult(int SubscriptionId, SubscriptionStatus Status,
    decimal RefundedAmount, string Message);

public sealed class SubscriptionCancellationService(IMembershipRepository repo, IUserRepository users,
    ISchedulingTransaction transaction, INotificationRepository notifications, IAppClock clock)
{
    private const string Version = "cancel-without-refund-v1";
    private const string Policy = "Gói ngừng hiệu lực ngay sau khi xác nhận hủy. Không hoàn tiền hoặc quy đổi thời hạn, buổi PT còn lại. Lịch sử đăng ký và thanh toán vẫn được giữ.";

    public async Task<SubscriptionCancellationPreview> PreviewAsync(int actorId, int subscriptionId, CancellationToken ct)
    {
        await EnsureMemberAsync(actorId);
        var sub = await RequireOwnedAsync(actorId, subscriptionId, ct);
        var reason = await UnavailableReasonAsync(sub, ct);
        return new(sub.Id, sub.Plan.Name, reason is null, reason, 0, Version, Policy);
    }

    public async Task<SubscriptionCancellationResult> CancelAsync(int actorId, int subscriptionId,
        CancelSubscriptionRequest request, CancellationToken ct)
    {
        await EnsureMemberAsync(actorId);
        if (!request.Confirmed || request.ExpectedRefundAmount != 0 || request.PolicyVersion != Version)
            throw SchedulingException.Conflict("CANCELLATION_CONFIRMATION_REQUIRED", "Vui lòng mở lại xác nhận và đọc chính sách hủy gói hiện tại.");
        return await transaction.ExecuteAsync(async token =>
        {
            // Booking, check-in, renewal and payment writers also hold this member lock.
            await transaction.LockAsync([new(ScheduleResourceType.Member, actorId)], token);
            var sub = await RequireOwnedAsync(actorId, subscriptionId, token);
            if (sub.Status == SubscriptionStatus.Cancelled) return Result(sub.Id);
            var reason = await UnavailableReasonAsync(sub, token);
            if (reason is not null) throw SchedulingException.Conflict("SUBSCRIPTION_CANCELLATION_BLOCKED", reason);
            sub.Status = SubscriptionStatus.Cancelled;
            // No wallet/order/payment changes. Remaining credits are preserved as historical data,
            // but Cancelled subscriptions cannot fund new bookings or check-ins.
            await repo.AddAuditAsync(actorId, "Subscription.MemberCancel", nameof(MemberSubscription), sub.Id,
                new { sub.PlanId, sub.StartDate, sub.EndDate, sub.RemainingPtSessions, RefundedAmount = 0m, PolicyVersion = Version }, token);
            notifications.AddNotification(actorId, actorId, NotificationType.General, "Đã hủy gói tập",
                $"Đã hủy gói {sub.Plan.Name}. Gói đã ngừng hiệu lực; không hoàn tiền.", $"subscription:{sub.Id}:member-cancel");
            return Result(sub.Id);
        }, ct);
    }

    private async Task EnsureMemberAsync(int actorId)
    {
        var actor = await users.GetByIdAsync(actorId);
        if (actor is not { Role: UserRole.Member, IsActive: true })
            throw SchedulingException.Forbidden("MEMBER_REQUIRED", "Chỉ hội viên đang hoạt động được tự hủy gói của mình.");
    }

    private async Task<MemberSubscription> RequireOwnedAsync(int actorId, int subscriptionId, CancellationToken ct) =>
        await repo.FindOwnedSubscriptionAsync(actorId, subscriptionId, ct)
        ?? throw SchedulingException.NotFound("SUBSCRIPTION_NOT_FOUND", "Không tìm thấy gói tập của bạn.");

    private async Task<string?> UnavailableReasonAsync(MemberSubscription sub, CancellationToken ct)
    {
        var localNow = clock.LocalNow;
        if (sub.Status != SubscriptionStatus.Active || sub.EndDate < DateOnly.FromDateTime(localNow))
            return "Chỉ có thể tự hủy gói còn hạn hoặc gói sắp bắt đầu. Gói chờ thanh toán cần được xử lý tại quầy.";
        var blockers = await repo.GetCancellationBlockersAsync(sub.Id, DateOnly.FromDateTime(localNow), TimeOnly.FromDateTime(localNow), ct);
        if (blockers.HasOpenGymVisit) return "Bạn đang check-in bằng gói này. Hãy nhờ lễ tân check-out trước khi hủy gói.";
        if (blockers.HasPendingPayment) return "Gói đang có giao dịch chờ thanh toán. Vui lòng liên hệ lễ tân xử lý trước khi hủy.";
        if (blockers.UpcomingClassBookings > 0 || blockers.UpcomingPtBookings > 0)
            return $"Gói còn {blockers.UpcomingClassBookings} lịch lớp và {blockers.UpcomingPtBookings} lịch PT chưa kết thúc. Hãy xử lý các lịch đó trong Lịch của tôi trước khi hủy gói.";
        return null;
    }

    private static SubscriptionCancellationResult Result(int id) => new(id, SubscriptionStatus.Cancelled, 0,
        "Đã hủy gói, không hoàn tiền. Xem lại đăng ký trong Lịch sử đăng ký.");
}
