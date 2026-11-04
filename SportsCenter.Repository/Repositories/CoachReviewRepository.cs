using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

public record CoachRatingStats(int CoachId, double Average, int Count);

public interface ICoachReviewRepository
{
    Task<Dictionary<int, CoachRatingStats>> GetStatsAsync(CancellationToken ct);
    Task<List<CoachReview>> ListForCoachAsync(int coachId, int take, CancellationToken ct);
    Task<List<CoachReview>> ListByMemberAsync(int memberId, CancellationToken ct);
    Task<CoachReview?> GetAsync(int coachId, int memberId, CancellationToken ct);
    Task<List<int>> ListTrainedCoachIdsAsync(int memberId, DateOnly today, TimeOnly now, CancellationToken ct);
    Task<List<User>> ListActiveCoachesAsync(IReadOnlyCollection<int> ids, CancellationToken ct);
    void Add(CoachReview review);
    Task SaveAsync(CancellationToken ct);
}

public sealed class CoachReviewRepository(AppDbContext db) : ICoachReviewRepository
{
    public Task<Dictionary<int, CoachRatingStats>> GetStatsAsync(CancellationToken ct) =>
        db.CoachReviews.GroupBy(r => r.CoachId)
            .Select(g => new CoachRatingStats(g.Key, g.Average(r => (double)r.Rating), g.Count()))
            .ToDictionaryAsync(s => s.CoachId, ct);

    public Task<List<CoachReview>> ListForCoachAsync(int coachId, int take, CancellationToken ct) =>
        db.CoachReviews.AsNoTracking().Include(r => r.Member).Where(r => r.CoachId == coachId)
            .OrderByDescending(r => r.UpdatedAt).Take(take).ToListAsync(ct);

    public Task<List<CoachReview>> ListByMemberAsync(int memberId, CancellationToken ct) =>
        db.CoachReviews.AsNoTracking().Where(r => r.MemberId == memberId).ToListAsync(ct);

    public Task<CoachReview?> GetAsync(int coachId, int memberId, CancellationToken ct) =>
        db.CoachReviews.FirstOrDefaultAsync(r => r.CoachId == coachId && r.MemberId == memberId, ct);

    // A coach counts as "trained with" once a non-cancelled PT booking, single-class booking or course session has ended.
    public async Task<List<int>> ListTrainedCoachIdsAsync(int memberId, DateOnly today, TimeOnly now, CancellationToken ct)
    {
        var pt = db.PtBookings.Where(b => b.MemberId == memberId && b.Status != BookingStatus.Cancelled
                && (b.Date < today || (b.Date == today && b.EndTime <= now)))
            .Select(b => (int?)b.CoachId);
        var single = db.ClassBookings.Where(b => b.MemberId == memberId && b.Status != BookingStatus.Cancelled
                && b.Session.Status != SessionStatus.Cancelled
                && (b.Session.Date < today || (b.Session.Date == today && b.Session.EndTime <= now)))
            .Select(b => b.Session.CoachId ?? b.Session.Class.CoachId);
        var course = db.ClassEnrollments.Where(e => e.MemberId == memberId && e.Status != EnrollmentStatus.Cancelled)
            .SelectMany(e => e.Class.Sessions)
            .Where(s => s.Status != SessionStatus.Cancelled && (s.Date < today || (s.Date == today && s.EndTime <= now)))
            .Select(s => s.CoachId ?? s.Class.CoachId);
        var ids = await pt.Union(single).Union(course).ToListAsync(ct); // UNION already de-duplicates
        return ids.OfType<int>().ToList();
    }

    public Task<List<User>> ListActiveCoachesAsync(IReadOnlyCollection<int> ids, CancellationToken ct) =>
        db.Users.AsNoTracking().Where(u => ids.Contains(u.Id) && u.Role == UserRole.Coach && u.IsActive)
            .OrderBy(u => u.FullName).ToListAsync(ct);

    public void Add(CoachReview review) => db.CoachReviews.Add(review);
    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
