using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

public class MembershipRepository(AppDbContext db) : IMembershipRepository
{
    public Task<List<MembershipPlan>> ListPlansAsync(bool activeOnly, CancellationToken ct) =>
        (activeOnly ? db.MembershipPlans.Where(x => x.IsActive) : db.MembershipPlans)
            .AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);

    public Task<MembershipPlan?> GetPlanAsync(int id, CancellationToken ct) =>
        db.MembershipPlans.FirstOrDefaultAsync(x => x.Id == id, ct);

    public Task<bool> PlanNameExistsAsync(string name, CancellationToken ct) =>
        db.MembershipPlans.AnyAsync(x => x.Name.ToLower() == name.ToLower(), ct);

    public Task<bool> PlanNameExistsForOtherAsync(string name, int excludeId, CancellationToken ct) =>
        db.MembershipPlans.AnyAsync(x => x.Id != excludeId && x.Name.ToLower() == name.ToLower(), ct);

    public async Task AddPlanAsync(MembershipPlan plan, CancellationToken ct) => await db.MembershipPlans.AddAsync(plan, ct);

    public Task<MemberSubscription?> GetLatestSubscriptionAsync(int memberId, int planId, CancellationToken ct) =>
        db.MemberSubscriptions.Where(x => x.MemberId == memberId && x.PlanId == planId)
            .OrderByDescending(x => x.EndDate).FirstOrDefaultAsync(ct);

    public Task<List<MemberSubscription>> ListSubscriptionsAsync(int memberId, CancellationToken ct) =>
        db.MemberSubscriptions.Include(x => x.Plan).AsNoTracking()
            .Where(x => x.MemberId == memberId).OrderByDescending(x => x.EndDate).ToListAsync(ct);

    public async Task AddSubscriptionAsync(MemberSubscription subscription, CancellationToken ct) =>
        await db.MemberSubscriptions.AddAsync(subscription, ct);

    public async Task AddPaymentAsync(Payment payment, CancellationToken ct) => await db.Payments.AddAsync(payment, ct);

    public Task<Payment?> GetPaymentWithDetailsAsync(int id, CancellationToken ct) =>
        db.Payments.Include(x => x.Member).Include(x => x.ReceivedBy)
            .Include(x => x.Subscription!).ThenInclude(x => x.Plan)
            .FirstOrDefaultAsync(x => x.Id == id, ct);

    public Task<Payment?> GetPaymentByOrderCodeAsync(long orderCode, CancellationToken ct) =>
        db.Payments.Include(x => x.Subscription).FirstOrDefaultAsync(x => x.PayOsOrderCode == orderCode, ct);

    public Task<PaymentKey?> GetPaymentKeyAsync(int id, CancellationToken ct) =>
        db.Payments.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new PaymentKey(x.MemberId)).FirstOrDefaultAsync(ct);

    public Task<PaymentKey?> GetPaymentKeyByOrderCodeAsync(long orderCode, CancellationToken ct) =>
        db.Payments.AsNoTracking().Where(x => x.PayOsOrderCode == orderCode)
            .Select(x => new PaymentKey(x.MemberId)).FirstOrDefaultAsync(ct);

    public Task AddAuditAsync(int actorId, string action, string entityName, int entityId, object details, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog
        {
            UserId = actorId,
            Action = action,
            EntityName = entityName,
            EntityId = entityId.ToString(),
            Details = JsonSerializer.Serialize(details),
            CreatedAt = DateTime.UtcNow,
        });
        return Task.CompletedTask;
    }

    public async Task<RevenueReportData> GetRevenueReportAsync(DateOnly from, DateOnly to, DateTime fromUtc, DateTime toUtcExclusive,
        DateOnly today, CancellationToken ct)
    {
        var revenue = await db.Payments
            .Where(p => p.Status == PaymentStatus.Paid && p.PaidAt >= fromUtc && p.PaidAt < toUtcExclusive)
            .GroupBy(_ => 1).Select(g => new { Total = g.Sum(x => x.Amount), Count = g.Count() })
            .FirstOrDefaultAsync(ct);
        var newMembers = await db.Users.CountAsync(u => u.Role == UserRole.Member && u.CreatedAt >= fromUtc && u.CreatedAt < toUtcExclusive, ct);
        var newSubscriptions = await db.MemberSubscriptions.CountAsync(s => s.StartDate >= from && s.StartDate <= to, ct);
        var activeSubscriptions = await db.MemberSubscriptions.CountAsync(s => s.Status == SubscriptionStatus.Active && s.EndDate >= today, ct);
        var expiredSubscriptions = await db.MemberSubscriptions.CountAsync(s =>
            s.Status == SubscriptionStatus.Expired || (s.Status == SubscriptionStatus.Active && s.EndDate < today), ct);
        var newClassBookings = await db.ClassBookings.CountAsync(b =>
            b.Status != BookingStatus.Cancelled && b.CreatedAt >= fromUtc && b.CreatedAt < toUtcExclusive, ct);

        return new RevenueReportData(revenue?.Total ?? 0, revenue?.Count ?? 0, newMembers, newSubscriptions,
            activeSubscriptions, expiredSubscriptions, newClassBookings);
    }

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
