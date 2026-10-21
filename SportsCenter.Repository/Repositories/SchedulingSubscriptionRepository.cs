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
    bool HasPendingPayment);

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
            .Where(x => x.MemberId == memberId && (x.Plan.SportId == sportId || x.Plan.SportId == null)))
        .ToListAsync(ct);

    public Task<List<SchedulingSubscriptionSnapshot>> ListForConfirmedClassBookingsAsync(
        int sessionId, CancellationToken ct) =>
        Project(db.ClassBookings.AsNoTracking()
            .Where(x => x.SessionId == sessionId && x.Status == BookingStatus.Confirmed)
            .Select(x => x.Subscription))
        .ToListAsync(ct);

    public Task<int> TryConsumePtCreditAsync(int subscriptionId, CancellationToken ct) =>
        db.MemberSubscriptions.Where(x => x.Id == subscriptionId && x.RemainingPtSessions > 0)
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.RemainingPtSessions, s => s.RemainingPtSessions - 1), ct);

    public Task<int> TryRefundPtCreditAsync(int subscriptionId, CancellationToken ct) =>
        db.MemberSubscriptions.Where(x => x.Id == subscriptionId &&
                                          x.RemainingPtSessions < x.Plan.PtSessionCredits)
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.RemainingPtSessions, s => s.RemainingPtSessions + 1), ct);

    private IQueryable<SchedulingSubscriptionSnapshot> Project(IQueryable<MemberSubscription> query) =>
        query.Select(x => new SchedulingSubscriptionSnapshot(
            x.Id,
            x.Status,
            x.StartDate,
            x.EndDate,
            x.Plan.SportId,
            x.Plan.IncludesClasses,
            x.Plan.PtSessionCredits,
            x.RemainingPtSessions,
            db.Payments.Any(p => p.SubscriptionId == x.Id && p.Status == PaymentStatus.Pending)));
}
