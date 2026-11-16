using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

public record RevenueReportData(decimal TotalRevenue, int PaymentCount, int NewMembers, int NewSubscriptions,
    int ActiveSubscriptions, int ExpiredSubscriptions, int NewClassBookings);
// One revenue line: a wallet order (item read from its Snapshot) or a paid legacy subscription payment (PlanId/PlanName).
public record RevenueSourceRow(CommerceOrderKind Kind, string? Snapshot, int? PlanId, string? PlanName, decimal Amount);
// Money log line: a wallet ledger entry (Source "wallet") or a top-up that has not reached the wallet yet (Source "payment").
public record MoneyLogRow(string Source, int SourceId, DateTime At, string Kind, PaymentStatus Status, int MemberId, string MemberName,
    int? ActorId, string? ActorName, UserRole? ActorRole, decimal Amount, decimal? BalanceAfter, PaymentMethod? Method, string Reference,
    int? OrderId, CommerceOrderKind? OrderKind, string? OrderSnapshot, int? PaymentId, string? InvoiceNumber,
    string? VoucherCode, decimal? ListPrice, decimal? DiscountAmount);
public record PaymentKey(int MemberId);
public record PayOsPaymentState(int MemberId, long OrderCode, PaymentStatus Status, PaymentMethod Method, bool HasCheckoutLink);
public record SubscriptionCancellationBlockers(bool HasOpenGymVisit, int UpcomingClassBookings, int UpcomingPtBookings,
    bool HasPendingPayment);
public record WalletReceiptRow(int PaymentId, string? InvoiceNumber, int MemberId, string MemberName,
    int? ReceivedById, string? ReceivedByName, decimal Amount, PaymentMethod Method, DateTime PaidAt);
public record WalletReceiptReportData(List<WalletReceiptRow> Items, int Total, decimal TotalAmount, decimal CashAmount, decimal BankTransferAmount);

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
    Task<MemberSubscription?> FindOwnedSubscriptionAsync(int memberId, int subscriptionId, CancellationToken ct);
    Task<SubscriptionCancellationBlockers> GetCancellationBlockersAsync(int subscriptionId, DateOnly today,
        TimeOnly localTime, CancellationToken ct);
    Task<Dictionary<int, PaymentStatus>> GetLatestPaymentStatusesAsync(IReadOnlyCollection<int> subscriptionIds,
        CancellationToken ct);
    Task<bool> HasPendingSubscriptionPaymentAsync(int memberId, int planId, CancellationToken ct);
    Task<Payment?> GetPendingSubscriptionPaymentAsync(int memberId, int planId, CancellationToken ct);
    Task AddSubscriptionAsync(MemberSubscription subscription, CancellationToken ct);

    Task AddPaymentAsync(Payment payment, CancellationToken ct);
    Task<Payment?> GetPaymentWithDetailsAsync(int id, CancellationToken ct);
    Task<List<Payment>> ListPendingCounterPaymentsAsync(CancellationToken ct);
    Task<List<int>> ListPendingPayOsPaymentIdsAsync(CancellationToken ct);
    Task<PaymentStatus?> GetPaymentStatusAsync(long orderCode, CancellationToken ct);
    Task<PayOsPaymentState?> GetPayOsStateAsync(int paymentId, CancellationToken ct);
    Task<Payment?> GetPaymentByOrderCodeAsync(long orderCode, CancellationToken ct);
    Task<PaymentKey?> GetPaymentKeyAsync(int id, CancellationToken ct);
    Task<PaymentKey?> GetPaymentKeyByOrderCodeAsync(long orderCode, CancellationToken ct);

    Task AddAuditAsync(int actorId, string action, string entityName, int entityId, object details, CancellationToken ct);

    Task<RevenueReportData> GetRevenueReportAsync(DateOnly from, DateOnly to, DateTime fromUtc, DateTime toUtcExclusive,
        DateOnly today, CancellationToken ct);
    Task<List<MoneyLogRow>> ListMoneyLogAsync(DateTime fromUtc, DateTime toUtcExclusive, string search, CancellationToken ct);
    Task<List<RevenueSourceRow>> ListRevenueSourcesAsync(DateTime fromUtc, DateTime toUtcExclusive, CancellationToken ct);
    Task<WalletReceiptReportData> GetWalletReceiptsAsync(DateTime fromUtc, DateTime toUtcExclusive,
        string search, int page, int pageSize, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}
