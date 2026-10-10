using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

// User lookups the scheduling services need (actor checks, coach eligibility). Shared user CRUD stays in IUserRepository.
public interface IScheduleUserRepository
{
    Task<User?> GetUserAsync(int id, CancellationToken ct);
    Task<bool> IsActiveInRoleAsync(int id, UserRole role, CancellationToken ct);
    Task<bool> IsCoachForSportAsync(int coachId, int sportId, CancellationToken ct);
    Task<bool> IsPtCoachAsync(int coachId, int? sportId, CancellationToken ct);
    Task<List<User>> ListPtCoachesAsync(int? sportId, CancellationToken ct);
    Task<List<int>> GetPtSportIdsAsync(int coachId, List<int> sportIds, CancellationToken ct);
}

public class ScheduleUserRepository(AppDbContext db) : IScheduleUserRepository
{
    public Task<User?> GetUserAsync(int id, CancellationToken ct) => db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);

    public Task<bool> IsActiveInRoleAsync(int id, UserRole role, CancellationToken ct) =>
        db.Users.AnyAsync(x => x.Id == id && x.Role == role && x.IsActive, ct);

    public Task<bool> IsCoachForSportAsync(int coachId, int sportId, CancellationToken ct) =>
        db.Users.AnyAsync(x => x.Id == coachId && x.Role == UserRole.Coach && x.IsActive && x.CoachProfile!.Sports.Any(s => s.Id == sportId), ct);

    public Task<bool> IsPtCoachAsync(int coachId, int? sportId, CancellationToken ct) =>
        db.Users.AnyAsync(x => x.Id == coachId && x.Role == UserRole.Coach && x.IsActive &&
            (sportId == null || x.CoachProfile!.Sports.Any(s => s.Id == sportId && s.Status == SportStatus.Active && s.HasPt)), ct);

    public Task<List<User>> ListPtCoachesAsync(int? sportId, CancellationToken ct)
    {
        var query = db.Users.AsNoTracking().Where(x => x.Role == UserRole.Coach && x.IsActive && x.CoachProfile != null &&
            x.CoachProfile.Sports.Any(s => s.Status == SportStatus.Active && s.HasPt));
        if (sportId is { } id) query = query.Where(x => x.CoachProfile!.Sports.Any(s => s.Id == id && s.Status == SportStatus.Active && s.HasPt));
        return query.Include(x => x.CoachProfile!).ThenInclude(x => x.Sports).OrderBy(x => x.FullName).ToListAsync(ct);
    }

    public Task<List<int>> GetPtSportIdsAsync(int coachId, List<int> sportIds, CancellationToken ct) =>
        db.CoachProfiles.Where(x => x.UserId == coachId).SelectMany(x => x.Sports)
            .Where(x => sportIds.Contains(x.Id) && x.Status == SportStatus.Active && x.HasPt).Select(x => x.Id).ToListAsync(ct);
}
