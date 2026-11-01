using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

public sealed record GymSubscriptionSnapshot(
    int Id,
    string PlanName,
    DateOnly StartDate,
    DateOnly EndDate,
    SubscriptionStatus Status,
    bool IncludesGymAccess,
    bool HasPaidPayment,
    bool HasPendingPayment,
    bool HasFailedPayment);

public sealed record GymMemberSearchRow(int MemberId, string FullName, string? Phone, string Email, string? CardCode, bool IsActive);

public interface IGymAccessRepository
{
    Task<User?> GetMemberAsync(int memberId, CancellationToken ct);
    Task<List<GymMemberSearchRow>> SearchMembersAsync(string term, int limit, CancellationToken ct);
    Task<User?> FindMemberByCardCodeAsync(string cardCode, CancellationToken ct);
    void AddMemberProfile(MemberProfile profile);
    Task<List<GymSubscriptionSnapshot>> ListSubscriptionsAsync(int memberId, CancellationToken ct);
    Task<CheckIn?> GetOpenVisitAsync(int memberId, CancellationToken ct);
    void AddVisit(CheckIn visit);
    // memberId null lists visits across all members (reception's today feed); otherwise scoped to one member.
    Task<(List<CheckIn> Items, int Total)> ListVisitsAsync(
        int? memberId, DateTime fromUtc, DateTime toUtcExclusive, int page, int pageSize, CancellationToken ct);
}

public sealed class GymAccessRepository(AppDbContext db) : IGymAccessRepository
{
    public Task<User?> GetMemberAsync(int memberId, CancellationToken ct) =>
        db.Users.Include(x => x.MemberProfile)
            .FirstOrDefaultAsync(x => x.Id == memberId && x.Role == UserRole.Member, ct);

    public Task<List<GymMemberSearchRow>> SearchMembersAsync(string term, int limit, CancellationToken ct)
    {
        var like = $"%{term}%";
        return db.Users.AsNoTracking()
            .Where(x => x.Role == UserRole.Member && (EF.Functions.ILike(x.FullName, like) || EF.Functions.ILike(x.Email, like) ||
                (x.Phone != null && EF.Functions.ILike(x.Phone, like)) ||
                (x.MemberProfile != null && x.MemberProfile.GymCardCode != null && EF.Functions.ILike(x.MemberProfile.GymCardCode, like))))
            .OrderBy(x => x.FullName).ThenBy(x => x.Id).Take(limit)
            .Select(x => new GymMemberSearchRow(x.Id, x.FullName, x.Phone, x.Email,
                x.MemberProfile != null ? x.MemberProfile.GymCardCode : null, x.IsActive))
            .ToListAsync(ct);
    }

    public Task<User?> FindMemberByCardCodeAsync(string cardCode, CancellationToken ct) =>
        db.Users.Include(x => x.MemberProfile)
            .FirstOrDefaultAsync(x => x.Role == UserRole.Member && x.MemberProfile != null &&
                x.MemberProfile.GymCardCode == cardCode, ct);

    public void AddMemberProfile(MemberProfile profile) => db.MemberProfiles.Add(profile);

    public Task<List<GymSubscriptionSnapshot>> ListSubscriptionsAsync(int memberId, CancellationToken ct) =>
        db.MemberSubscriptions.AsNoTracking().Where(x => x.MemberId == memberId)
            .Select(x => new GymSubscriptionSnapshot(
                x.Id,
                x.Plan.Name,
                x.StartDate,
                x.EndDate,
                x.Status,
                x.IncludesGymAccess,
                db.Payments.Any(p => p.SubscriptionId == x.Id && p.Status == PaymentStatus.Paid),
                db.Payments.Any(p => p.SubscriptionId == x.Id && p.Status == PaymentStatus.Pending),
                db.Payments.Any(p => p.SubscriptionId == x.Id && p.Status == PaymentStatus.Failed)))
            .ToListAsync(ct);

    public Task<CheckIn?> GetOpenVisitAsync(int memberId, CancellationToken ct) =>
        db.CheckIns.FirstOrDefaultAsync(x => x.MemberId == memberId && x.CheckedOutAt == null, ct);

    public void AddVisit(CheckIn visit) => db.CheckIns.Add(visit);

    public async Task<(List<CheckIn> Items, int Total)> ListVisitsAsync(
        int? memberId, DateTime fromUtc, DateTime toUtcExclusive, int page, int pageSize, CancellationToken ct)
    {
        var query = db.CheckIns.AsNoTracking().Where(x =>
            (memberId == null || x.MemberId == memberId) &&
            x.CheckedInAt >= fromUtc && x.CheckedInAt < toUtcExclusive);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.CheckedInAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Include(x => x.Member).Include(x => x.Subscription!).ThenInclude(x => x.Plan)
            .Include(x => x.CheckedInBy).Include(x => x.CheckedOutBy)
            .ToListAsync(ct);
        return (items, total);
    }
}
