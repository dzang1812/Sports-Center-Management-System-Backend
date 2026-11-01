using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

public interface ICoachProfileRepository
{
    Task<User?> GetActiveCoachAsync(int coachId, bool tracking, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
}

public sealed class CoachProfileRepository(AppDbContext db) : ICoachProfileRepository
{
    public Task<User?> GetActiveCoachAsync(int coachId, bool tracking, CancellationToken ct)
    {
        var coaches = db.Users.Where(u => u.Id == coachId && u.IsActive && u.Role == UserRole.Coach)
            .Include(u => u.CoachProfile!).ThenInclude(p => p.Sports);
        return (tracking ? coaches : coaches.AsNoTracking()).FirstOrDefaultAsync(ct);
    }

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
