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
        int? asId = null;
        if (!string.IsNullOrWhiteSpace(term))
        {
            term = term.Trim().ToLower();
            asId = int.TryParse(term.TrimStart('#'), out var id) ? id : null; // "#12" or "12" = member id
            query = query.Where(u => u.FullName.ToLower().Contains(term) || u.Email.Contains(term) ||
                (u.Phone != null && u.Phone.Contains(term)) || (asId != null && u.Id == asId));
        }

        var total = await query.CountAsync();
        // Exact id match first so an id lookup is never pushed off the page by phone/email partial matches.
        var items = await query.OrderBy(u => u.Id == asId ? 0 : 1).ThenBy(u => u.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Include(u => u.CoachProfile!.Sports).ToListAsync();
        return (items, total);
    }

    public Task<User?> GetByIdWithCoachAsync(int id) =>
        db.Users.Include(u => u.CoachProfile!.Sports).FirstOrDefaultAsync(u => u.Id == id);

    public Task<User?> GetByIdIncludingLockedAsync(int id) => db.Users.FirstOrDefaultAsync(u => u.Id == id);

    public Task<List<Sport>> GetSportsByNamesAsync(string[] names) =>
        db.Sports.Where(s => names.Contains(s.Name)).ToListAsync();
}
