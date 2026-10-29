using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

public sealed record SchedulingSubscriptionSnapshot(
    int Id,
    SubscriptionStatus Status,
    DateOnly StartDate,
    DateOnly EndDate,
    int? SportId,
    bool IncludesClasses,
    int PtSessionCredits,
    int RemainingPtSessions,
    bool HasPendingPayment, bool IsWalletPurchase = false);

public interface ISchedulingSubscriptionRepository
{
    Task<List<SchedulingSubscriptionSnapshot>> ListForMemberAndSportAsync(
        int memberId, int sportId, CancellationToken ct);
    Task<List<SchedulingSubscriptionSnapshot>> ListForConfirmedClassBookingsAsync(
        int sessionId, CancellationToken ct);
    Task<int> TryConsumePtCreditAsync(int subscriptionId, CancellationToken ct);
    Task<int> TryRefundPtCreditAsync(int subscriptionId, CancellationToken ct);
}

public sealed class SchedulingSubscriptionRepository(AppDbContext db) : ISchedulingSubscriptionRepository
{
    public Task<List<SchedulingSubscriptionSnapshot>> ListForMemberAndSportAsync(
        int memberId, int sportId, CancellationToken ct) =>
        Project(db.MemberSubscriptions.AsNoTracking()
            .Where(x => x.MemberId == memberId && ((x.PurchaseOrderId != null ? x.SnapshotSportId : x.Plan.SportId) == sportId || (x.PurchaseOrderId != null ? x.SnapshotSportId : x.Plan.SportId) == null)))
        .ToListAsync(ct);

    public Task<List<SchedulingSubscriptionSnapshot>> ListForConfirmedClassBookingsAsync(
        int sessionId, CancellationToken ct) =>
        Project(db.ClassBookings.AsNoTracking()
            .Where(x => x.SessionId == sessionId && x.Status == BookingStatus.Confirmed && x.SubscriptionId != null)
            .Select(x => x.Subscription!))
        .ToListAsync(ct);

    public Task<int> TryConsumePtCreditAsync(int subscriptionId, CancellationToken ct) =>
        db.MemberSubscriptions.Where(x => x.Id == subscriptionId && x.RemainingPtSessions > 0)
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.RemainingPtSessions, s => s.RemainingPtSessions - 1).SetProperty(s => s.EverUsedPtCredit, true), ct);

    public Task<int> TryRefundPtCreditAsync(int subscriptionId, CancellationToken ct) =>
        db.MemberSubscriptions.Where(x => x.Id == subscriptionId &&
                                          x.RemainingPtSessions < (x.SnapshotPtCredits ?? x.Plan.PtSessionCredits))
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.RemainingPtSessions, s => s.RemainingPtSessions + 1), ct);

    private IQueryable<SchedulingSubscriptionSnapshot> Project(IQueryable<MemberSubscription> query) =>
        query.Select(x => new SchedulingSubscriptionSnapshot(
            x.Id,
            x.Status,
            x.StartDate,
            x.EndDate,
            x.PurchaseOrderId != null ? x.SnapshotSportId : x.Plan.SportId,
            x.PurchaseOrderId == null && x.Plan.IncludesClasses,
            x.SnapshotPtCredits ?? x.Plan.PtSessionCredits,
            x.RemainingPtSessions,
            db.Payments.Any(p => p.SubscriptionId == x.Id && p.Status == PaymentStatus.Pending), x.PurchaseOrderId != null));
}
