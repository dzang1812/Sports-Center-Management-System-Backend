using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

public interface ITrainingRepository
{
    Task<List<Exercise>> ExercisesAsync(string? muscleGroup, string? search, CancellationToken ct);
    Task<List<string>> MuscleGroupsAsync(CancellationToken ct);
    Task<List<Exercise>> TrackedExercisesAsync(CancellationToken ct);
    void AddExercise(Exercise exercise);
    Task<int> CountExercisesAsync(IEnumerable<int> ids, CancellationToken ct);
    // Member view: own plans + coach plans for them + coach plans for classes they attend.
    Task<List<TrainingPlan>> PlansAsync(int memberId, CancellationToken ct);
    // Self-service plans only (CoachId null): coach-made plans are read-only for the member.
    Task<TrainingPlan?> OwnedPlanAsync(int memberId, int planId, CancellationToken ct);
    Task DeactivatePlansAsync(int memberId, CancellationToken ct);
    void AddPlan(TrainingPlan plan);
    void RemovePlan(TrainingPlan plan);
    Task SaveChangesAsync(CancellationToken ct);

    Task<bool> CoachTeachesMemberAsync(int coachId, int memberId, CancellationToken ct);
    Task<bool> CoachTeachesClassAsync(int coachId, int classId, CancellationToken ct);
    Task<List<User>> CoachStudentsAsync(int coachId, string? search, CancellationToken ct);
    Task<List<Class>> CoachClassesAsync(int coachId, CancellationToken ct);
    Task<List<TrainingPlan>> CoachPlansAsync(int coachId, int? memberId, int? classId, CancellationToken ct);
    Task<TrainingPlan?> CoachOwnedPlanAsync(int coachId, int planId, CancellationToken ct);

    Task<ClassSession?> ClassSessionAsync(int sessionId, CancellationToken ct);
    Task<List<int>> ClassSessionMemberIdsAsync(int sessionId, CancellationToken ct);
    Task<PtBooking?> PtBookingAsync(int bookingId, CancellationToken ct);
    Task<List<TrainingResult>> TrackedResultsAsync(int? sessionId, int? ptBookingId, CancellationToken ct);
    Task<List<TrainingResult>> ResultsAsync(int? sessionId, int? ptBookingId, int? memberId, CancellationToken ct);
    void AddResults(IEnumerable<TrainingResult> results);
    void RemoveResults(IEnumerable<TrainingResult> results);
}

public class TrainingRepository(AppDbContext db) : ITrainingRepository
{
    public Task<List<Exercise>> ExercisesAsync(string? muscleGroup, string? search, CancellationToken ct)
    {
        var q = db.Exercises.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(muscleGroup)) q = q.Where(x => x.MuscleGroup == muscleGroup);
        if (!string.IsNullOrWhiteSpace(search)) q = q.Where(x => EF.Functions.ILike(x.Name, $"%{search.Trim()}%"));
        return q.OrderBy(x => x.MuscleGroup).ThenBy(x => x.Name).ToListAsync(ct);
    }

    public Task<List<string>> MuscleGroupsAsync(CancellationToken ct) =>
        db.Exercises.Where(x => x.MuscleGroup != null).Select(x => x.MuscleGroup!).Distinct().OrderBy(x => x).ToListAsync(ct);

    public Task<List<Exercise>> TrackedExercisesAsync(CancellationToken ct) => db.Exercises.ToListAsync(ct);

    public void AddExercise(Exercise exercise) => db.Exercises.Add(exercise);

    public Task<int> CountExercisesAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToList();
        return db.Exercises.CountAsync(x => distinct.Contains(x.Id), ct);
    }

    public Task<List<TrainingPlan>> PlansAsync(int memberId, CancellationToken ct) =>
        WithDetails(db.TrainingPlans.AsNoTracking())
            .Where(x => x.MemberId == memberId || x.ClassId != null &&
                (db.ClassBookings.Any(b => b.MemberId == memberId && b.Status != BookingStatus.Cancelled && b.Session.ClassId == x.ClassId) ||
                 db.ClassEnrollments.Any(e => e.MemberId == memberId && e.Status != EnrollmentStatus.Cancelled && e.ClassId == x.ClassId)))
            .OrderByDescending(x => x.IsActive).ThenByDescending(x => x.Id).ToListAsync(ct);

    public Task<TrainingPlan?> OwnedPlanAsync(int memberId, int planId, CancellationToken ct) =>
        db.TrainingPlans.AsSplitQuery()
            .Include(x => x.Sessions).Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == planId && x.MemberId == memberId && x.CoachId == null, ct);

    public Task DeactivatePlansAsync(int memberId, CancellationToken ct) =>
        db.TrainingPlans.Where(x => x.MemberId == memberId && x.CoachId == null && x.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), ct);

    public void AddPlan(TrainingPlan plan) => db.TrainingPlans.Add(plan);

    public void RemovePlan(TrainingPlan plan) => db.TrainingPlans.Remove(plan);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    // "Teaches" = a live class booking in a session the coach runs (session coach, else class coach),
    // a live enrollment in a course the coach leads, or a live PT booking with the coach.
    public Task<bool> CoachTeachesMemberAsync(int coachId, int memberId, CancellationToken ct) =>
        db.Users.AnyAsync(u => u.Id == memberId && u.Role == UserRole.Member && TaughtBy(coachId).Contains(u.Id), ct);

    public Task<bool> CoachTeachesClassAsync(int coachId, int classId, CancellationToken ct) =>
        CoachClassesQuery(coachId).AnyAsync(c => c.Id == classId, ct);

    public Task<List<User>> CoachStudentsAsync(int coachId, string? search, CancellationToken ct)
    {
        var q = db.Users.AsNoTracking().Include(u => u.MemberProfile)
            .Where(u => u.Role == UserRole.Member && TaughtBy(coachId).Contains(u.Id));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var like = $"%{search.Trim()}%";
            q = q.Where(u => EF.Functions.ILike(EF.Functions.Unaccent(u.FullName), EF.Functions.Unaccent(like)) ||
                EF.Functions.ILike(u.Email, like) || u.Phone != null && EF.Functions.ILike(u.Phone, like));
        }
        return q.OrderBy(u => u.FullName).ThenBy(u => u.Id).ToListAsync(ct);
    }

    public Task<List<Class>> CoachClassesAsync(int coachId, CancellationToken ct) =>
        CoachClassesQuery(coachId).AsNoTracking().Include(c => c.Sport).OrderBy(c => c.Name).ToListAsync(ct);

    public Task<List<TrainingPlan>> CoachPlansAsync(int coachId, int? memberId, int? classId, CancellationToken ct) =>
        WithDetails(db.TrainingPlans.AsNoTracking())
            .Where(x => x.CoachId == coachId && (memberId == null || x.MemberId == memberId) && (classId == null || x.ClassId == classId))
            .OrderByDescending(x => x.Id).ToListAsync(ct);

    public Task<TrainingPlan?> CoachOwnedPlanAsync(int coachId, int planId, CancellationToken ct) =>
        db.TrainingPlans.AsSplitQuery().Include(x => x.Sessions).Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == planId && x.CoachId == coachId, ct);

    public Task<ClassSession?> ClassSessionAsync(int sessionId, CancellationToken ct) =>
        db.ClassSessions.AsNoTracking().Include(s => s.Class).FirstOrDefaultAsync(s => s.Id == sessionId, ct);

    public Task<List<int>> ClassSessionMemberIdsAsync(int sessionId, CancellationToken ct) =>
        db.ClassBookings.Where(b => b.SessionId == sessionId && b.Status != BookingStatus.Cancelled).Select(b => b.MemberId).ToListAsync(ct);

    public Task<PtBooking?> PtBookingAsync(int bookingId, CancellationToken ct) =>
        db.PtBookings.AsNoTracking().FirstOrDefaultAsync(p => p.Id == bookingId, ct);

    public Task<List<TrainingResult>> TrackedResultsAsync(int? sessionId, int? ptBookingId, CancellationToken ct) =>
        db.TrainingResults.Where(r => sessionId != null ? r.SessionId == sessionId : r.PtBookingId == ptBookingId).ToListAsync(ct);

    public Task<List<TrainingResult>> ResultsAsync(int? sessionId, int? ptBookingId, int? memberId, CancellationToken ct) =>
        db.TrainingResults.AsNoTracking()
            .Include(r => r.Member).Include(r => r.RecordedBy).Include(r => r.Exercise)
            .Include(r => r.Session!).ThenInclude(s => s.Class).Include(r => r.PtBooking!).ThenInclude(p => p.Sport)
            .Where(r => (sessionId == null || r.SessionId == sessionId) && (ptBookingId == null || r.PtBookingId == ptBookingId) &&
                (memberId == null || r.MemberId == memberId))
            .OrderByDescending(r => r.RecordedAt).ThenBy(r => r.Id).ToListAsync(ct);

    public void AddResults(IEnumerable<TrainingResult> results) => db.TrainingResults.AddRange(results);

    public void RemoveResults(IEnumerable<TrainingResult> results) => db.TrainingResults.RemoveRange(results);

    private IQueryable<int> TaughtBy(int coachId) =>
        db.ClassBookings.Where(b => b.Status != BookingStatus.Cancelled &&
                (b.Session.CoachId == coachId || b.Session.CoachId == null && b.Session.Class.CoachId == coachId))
            .Select(b => b.MemberId)
            .Union(db.ClassEnrollments.Where(e => e.Status != EnrollmentStatus.Cancelled && e.Class.CoachId == coachId).Select(e => e.MemberId))
            .Union(db.PtBookings.Where(p => p.Status != BookingStatus.Cancelled && p.CoachId == coachId).Select(p => p.MemberId));

    private IQueryable<Class> CoachClassesQuery(int coachId) =>
        db.Classes.Where(c => c.CoachId == coachId || db.ClassSessions.Any(s => s.ClassId == c.Id && s.CoachId == coachId));

    private static IQueryable<TrainingPlan> WithDetails(IQueryable<TrainingPlan> q) =>
        q.AsSplitQuery().Include(x => x.Sessions).Include(x => x.Items).ThenInclude(i => i.Exercise)
            .Include(x => x.Coach).Include(x => x.Member).Include(x => x.Class);
}
