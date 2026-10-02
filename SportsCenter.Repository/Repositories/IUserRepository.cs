using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

public interface IUserRepository
{
    Task<User?> GetByEmailAsync(string email);
    Task<User?> GetByIdWithProfileAsync(int id);
    Task<User?> GetByIdAsync(int id);
    Task AddTokenAsync(EmailToken token);
    Task<EmailToken?> GetTokenAsync(string hash, TokenPurpose purpose);
    Task<bool> EmailExistsAsync(string email);
    Task AddAsync(User user);
    Task SaveChangesAsync();
}
