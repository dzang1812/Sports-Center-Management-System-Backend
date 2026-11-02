using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

public record ClassFillRow(int ClassId, string Name, string? Coach, ClassStatus Status, ClassBookingMode BookingMode, int Capacity,
    int ActiveEnrollments, int Sessions, int ActiveBookings);

public record ExpiringSubscriptionRow(int SubscriptionId, int MemberId, string PlanName, DateOnly EndDate, string MemberName);

public interface IFrontDeskRepository
{
    void AddSupportRequest(SupportRequest request);
    void AddSupportMessage(SupportMessage message);
    Task<SupportRequest?> GetSupportRequestAsync(int id, CancellationToken ct);
    Task<List<SupportMessage>> ListSupportMessagesAsync(int requestId, bool includeInternal, CancellationToken ct);
    Task<(List<SupportRequest> Items, int Total)> ListSupportRequestsAsync(int? memberId, SupportStatus? status, SupportCategory? category,
        string? q, int page, int pageSize, CancellationToken ct);
    Task<Dictionary<SupportStatus, int>> CountSupportByStatusAsync(CancellationToken ct);

    Task<(List<Payment> Items, int Total)> ListPaymentsAsync(int memberId, int page, int pageSize, CancellationToken ct);

    Task<List<(DateTime At, decimal Amount)>> ListRevenueAsync(DateTime fromUtc, DateTime toUtcExclusive, CancellationToken ct);
    Task<List<DateTime>> ListNewMemberTimesAsync(DateTime fromUtc, DateTime toUtcExclusive, CancellationToken ct);
    Task<List<DateTime>> ListBookingTimesAsync(DateTime fromUtc, DateTime toUtcExclusive, CancellationToken ct);
    Task<List<ClassFillRow>> ListClassFillAsync(CancellationToken ct);

    Task<ClassSession?> GetSessionWithClassAsync(int sessionId, CancellationToken ct);
    Task<Class?> GetClassAsync(int classId, CancellationToken ct);
    Task<List<int>> ListSessionMemberIdsAsync(int sessionId, CancellationToken ct);
    Task<List<int>> ListClassMemberIdsAsync(int classId, CancellationToken ct);

    Task<List<ExpiringSubscriptionRow>> ListExpiringSubscriptionsAsync(DateOnly from, DateOnly to, CancellationToken ct);
    Task<HashSet<string>> ExistingNotificationKeysAsync(IReadOnlyCollection<string> keys, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}

public class FrontDeskRepository(AppDbContext db) : IFrontDeskRepository
{
    public void AddSupportRequest(SupportRequest request) => db.SupportRequests.Add(request);

    public void AddSupportMessage(SupportMessage message) => db.SupportMessages.Add(message);

    public Task<SupportRequest?> GetSupportRequestAsync(int id, CancellationToken ct) =>
        db.SupportRequests.Include(x => x.Member).Include(x => x.HandledBy).Include(x => x.CreatedBy).FirstOrDefaultAsync(x => x.Id == id, ct);

    public Task<List<SupportMessage>> ListSupportMessagesAsync(int requestId, bool includeInternal, CancellationToken ct) =>
        db.SupportMessages.AsNoTracking().Include(x => x.Author)
            .Where(x => x.RequestId == requestId && (includeInternal || !x.IsInternal))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(ct);

    public async Task<Dictionary<SupportStatus, int>> CountSupportByStatusAsync(CancellationToken ct) =>
        (await db.SupportRequests.GroupBy(x => x.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct))
            .ToDictionary(x => x.Key, x => x.Count);

    public async Task<(List<SupportRequest> Items, int Total)> ListSupportRequestsAsync(int? memberId, SupportStatus? status, SupportCategory? category,
        string? q, int page, int pageSize, CancellationToken ct)
    {
        var query = db.SupportRequests.AsNoTracking();
        if (memberId is { } id) query = query.Where(x => x.MemberId == id);
        if (status is { } st) query = query.Where(x => x.Status == st);
        if (category is { } cat) query = query.Where(x => x.Category == cat);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = $"%{q.Trim()}%";
            query = query.Where(x => EF.Functions.ILike(x.Subject, term) || EF.Functions.ILike(x.Member.FullName, term) ||
                EF.Functions.ILike(x.Member.Email, term));
        }
        var total = await query.CountAsync(ct);
        // Needs-action first: new, in progress, waiting on member, then done.
        var items = await query.Include(x => x.Member).Include(x => x.HandledBy).Include(x => x.CreatedBy)
            .OrderBy(x => x.Status == SupportStatus.Open ? 0 : x.Status == SupportStatus.InProgress ? 1 : x.Status == SupportStatus.WaitingForMember ? 2 : 3)
            .ThenByDescending(x => x.LastActivityAt)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    public async Task<(List<Payment> Items, int Total)> ListPaymentsAsync(int memberId, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Payments.AsNoTracking().Where(x => x.MemberId == memberId);
        var total = await query.CountAsync(ct);
        var items = await query.Include(x => x.Member).Include(x => x.ReceivedBy)
            .Include(x => x.Subscription!).ThenInclude(x => x.Plan)
            .OrderByDescending(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    // Same revenue definition as MembershipRepository.GetRevenueReportAsync: paid non-top-up payments + net commerce orders.
    public async Task<List<(DateTime At, decimal Amount)>> ListRevenueAsync(DateTime fromUtc, DateTime toUtcExclusive, CancellationToken ct)
    {
        var payments = await db.Payments
            .Where(p => p.Purpose != PaymentPurpose.WalletTopUp && p.Status == PaymentStatus.Paid && p.PaidAt >= fromUtc && p.PaidAt < toUtcExclusive)
            .Select(p => new { p.PaidAt, p.Amount }).ToListAsync(ct);
        var orders = await db.CommerceOrders.Where(x => x.CreatedAt >= fromUtc && x.CreatedAt < toUtcExclusive)
            .Select(x => new { x.CreatedAt, Amount = x.PaidAmount - x.RefundedAmount }).ToListAsync(ct);
        return [.. payments.Select(p => (p.PaidAt, p.Amount)), .. orders.Select(o => (o.CreatedAt, o.Amount))];
    }

    public Task<List<DateTime>> ListNewMemberTimesAsync(DateTime fromUtc, DateTime toUtcExclusive, CancellationToken ct) =>
        db.Users.Where(u => u.Role == UserRole.Member && u.CreatedAt >= fromUtc && u.CreatedAt < toUtcExclusive)
            .Select(u => u.CreatedAt).ToListAsync(ct);

    public Task<List<DateTime>> ListBookingTimesAsync(DateTime fromUtc, DateTime toUtcExclusive, CancellationToken ct) =>
        db.ClassBookings.Where(b => b.Status != BookingStatus.Cancelled && b.CreatedAt >= fromUtc && b.CreatedAt < toUtcExclusive)
            .Select(b => b.CreatedAt).ToListAsync(ct);

    public Task<List<ClassFillRow>> ListClassFillAsync(CancellationToken ct) =>
        db.Classes.AsNoTracking().Where(c => c.Status != ClassStatus.Draft)
            .OrderBy(c => c.Status).ThenBy(c => c.Name)
            .Select(c => new ClassFillRow(c.Id, c.Name, c.Coach != null ? c.Coach.FullName : null, c.Status, c.BookingMode, c.Capacity,
                db.ClassEnrollments.Count(e => e.ClassId == c.Id && e.Status != EnrollmentStatus.Cancelled),
                db.ClassSessions.Count(s => s.ClassId == c.Id && s.Status != SessionStatus.Cancelled),
                db.ClassBookings.Count(b => b.Session.ClassId == c.Id && b.Status != BookingStatus.Cancelled && b.Session.Status != SessionStatus.Cancelled)))
            .ToListAsync(ct);

    public Task<ClassSession?> GetSessionWithClassAsync(int sessionId, CancellationToken ct) =>
        db.ClassSessions.AsNoTracking().Include(x => x.Class).FirstOrDefaultAsync(x => x.Id == sessionId, ct);

    public Task<Class?> GetClassAsync(int classId, CancellationToken ct) =>
        db.Classes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == classId, ct);

    public Task<List<int>> ListSessionMemberIdsAsync(int sessionId, CancellationToken ct) =>
        db.ClassBookings.Where(b => b.SessionId == sessionId && b.Status != BookingStatus.Cancelled)
            .Select(b => b.MemberId).Distinct().ToListAsync(ct);

    public Task<List<int>> ListClassMemberIdsAsync(int classId, CancellationToken ct) =>
        db.ClassBookings.Where(b => b.Session.ClassId == classId && b.Status != BookingStatus.Cancelled).Select(b => b.MemberId)
            .Union(db.ClassEnrollments.Where(e => e.ClassId == classId && e.Status != EnrollmentStatus.Cancelled).Select(e => e.MemberId))
            .ToListAsync(ct);

    // A plan the member already renewed (a later active subscription of the same plan) is not "expiring".
    public Task<List<ExpiringSubscriptionRow>> ListExpiringSubscriptionsAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        db.MemberSubscriptions.AsNoTracking()
            .Where(s => s.Status == SubscriptionStatus.Active && s.EndDate >= from && s.EndDate <= to &&
                !db.MemberSubscriptions.Any(o => o.MemberId == s.MemberId && o.PlanId == s.PlanId &&
                    o.Status == SubscriptionStatus.Active && o.EndDate > s.EndDate))
            .OrderBy(s => s.EndDate).ThenBy(s => s.Id)
            .Select(s => new ExpiringSubscriptionRow(s.Id, s.MemberId, s.Plan.Name, s.EndDate, s.Member.FullName)).ToListAsync(ct);

    public async Task<HashSet<string>> ExistingNotificationKeysAsync(IReadOnlyCollection<string> keys, CancellationToken ct) =>
        [.. await db.Notifications.Where(n => n.DeduplicationKey != null && keys.Contains(n.DeduplicationKey))
            .Select(n => n.DeduplicationKey!).ToListAsync(ct)];

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
