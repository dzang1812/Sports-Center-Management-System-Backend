using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

// Manager-side user management queries (USR-01/02/03). Shared lookups (email, add, save) stay in IUserRepository.
public interface IUserAdminRepository
{
    Task<(List<User> Items, int Total)> SearchAsync(UserRole? role, bool? active, string? term, int page, int pageSize);
    Task<User?> GetByIdWithCoachAsync(int id);
    Task<User?> GetByIdIncludingLockedAsync(int id);
    Task<List<Sport>> GetSportsByNamesAsync(string[] names);
}
