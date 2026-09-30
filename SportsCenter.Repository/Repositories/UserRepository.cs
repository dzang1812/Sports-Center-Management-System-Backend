using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

public class UserRepository(AppDbContext db) : IUserRepository
{
    public Task<User?> GetByEmailAsync(string email) =>
        db.Users.FirstOrDefaultAsync(u => u.Email == email);

    public Task<User?> GetByIdWithProfileAsync(int id) =>
        db.Users.Include(u => u.MemberProfile).FirstOrDefaultAsync(u => u.Id == id && u.IsActive);

    public Task<User?> GetByIdAsync(int id) => db.Users.FirstOrDefaultAsync(u => u.Id == id && u.IsActive);

    public async Task AddTokenAsync(EmailToken token) => await db.EmailTokens.AddAsync(token);

    public Task<EmailToken?> GetTokenAsync(string hash, TokenPurpose purpose) =>
        db.EmailTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == hash && t.Purpose == purpose);

    public Task<bool> EmailExistsAsync(string email) =>
        db.Users.AnyAsync(u => u.Email == email);

    public async Task AddAsync(User user) => await db.Users.AddAsync(user);

    public Task SaveChangesAsync() => db.SaveChangesAsync();
}
