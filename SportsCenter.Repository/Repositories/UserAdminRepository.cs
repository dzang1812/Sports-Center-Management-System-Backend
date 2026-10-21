using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

public class UserAdminRepository(AppDbContext db) : IUserAdminRepository
{
    public async Task<(List<User> Items, int Total)> SearchAsync(UserRole? role, bool? active, string? term, int page, int pageSize)
    {
        var query = db.Users.AsNoTracking().AsQueryable();
        if (role is { } r) query = query.Where(u => u.Role == r);
        if (active is { } a) query = query.Where(u => u.IsActive == a);
        if (!string.IsNullOrWhiteSpace(term))
        {
            term = term.Trim().ToLower();
            var asId = int.TryParse(term, out var id) ? id : (int?)null;
            query = query.Where(u => u.FullName.ToLower().Contains(term) || u.Email.Contains(term) ||
                (u.Phone != null && u.Phone.Contains(term)) || (asId != null && u.Id == asId));
        }

        var total = await query.CountAsync();
        var items = await query.OrderBy(u => u.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Include(u => u.CoachProfile!.Sports).ToListAsync();
        return (items, total);
    }

    public Task<User?> GetByIdWithCoachAsync(int id) =>
        db.Users.Include(u => u.CoachProfile!.Sports).FirstOrDefaultAsync(u => u.Id == id);

    public Task<User?> GetByIdIncludingLockedAsync(int id) => db.Users.FirstOrDefaultAsync(u => u.Id == id);

    public Task<List<Sport>> GetSportsByNamesAsync(string[] names) =>
        db.Sports.Where(s => names.Contains(s.Name)).ToListAsync();
}
