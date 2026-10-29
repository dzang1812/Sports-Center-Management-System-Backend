using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

public record RevenueReportData(decimal TotalRevenue, int PaymentCount, int NewMembers, int NewSubscriptions,
    int ActiveSubscriptions, int ExpiredSubscriptions, int NewClassBookings);
public record PaymentKey(int MemberId);

public interface IMembershipRepository
{
    Task<List<MembershipPlan>> ListPlansAsync(bool activeOnly, CancellationToken ct);
    Task<MembershipPlan?> GetPlanAsync(int id, CancellationToken ct);
    Task<List<Sport>> ListSportsAsync(CancellationToken ct);
    Task<bool> PlanNameExistsAsync(string name, CancellationToken ct);
    Task<bool> PlanNameExistsForOtherAsync(string name, int excludeId, CancellationToken ct);
    Task<bool> PlanHasSubscriptionsAsync(int planId, CancellationToken ct);
    Task AddPlanAsync(MembershipPlan plan, CancellationToken ct);

    Task<MemberSubscription?> GetLatestSubscriptionAsync(int memberId, int planId, CancellationToken ct);
    Task<List<MemberSubscription>> ListSubscriptionsAsync(int memberId, CancellationToken ct);
    Task<Dictionary<int, PaymentStatus>> GetLatestPaymentStatusesAsync(IReadOnlyCollection<int> subscriptionIds,
        CancellationToken ct);
    Task<Payment?> FindPendingSubscriptionPaymentAsync(int memberId, int planId, CancellationToken ct);
    Task<bool> HasPendingSubscriptionPaymentAsync(int memberId, int planId, CancellationToken ct);
    Task AddSubscriptionAsync(MemberSubscription subscription, CancellationToken ct);

    Task AddPaymentAsync(Payment payment, CancellationToken ct);
    Task<Payment?> GetPaymentWithDetailsAsync(int id, CancellationToken ct);
    Task<List<Payment>> ListPendingCounterPaymentsAsync(CancellationToken ct);
    Task<Payment?> GetPaymentByOrderCodeAsync(long orderCode, CancellationToken ct);
    Task<PaymentKey?> GetPaymentKeyAsync(int id, CancellationToken ct);
    Task<PaymentKey?> GetPaymentKeyByOrderCodeAsync(long orderCode, CancellationToken ct);

    Task AddAuditAsync(int actorId, string action, string entityName, int entityId, object details, CancellationToken ct);

    Task<RevenueReportData> GetRevenueReportAsync(DateOnly from, DateOnly to, DateTime fromUtc, DateTime toUtcExclusive,
        DateOnly today, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}
