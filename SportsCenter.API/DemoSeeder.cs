using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.API;

// Development only. Idempotent: skips accounts/sports that already exist. All demo accounts share the password below.
public static class DemoSeeder
{
    private const string Password = "Demo@123";

    public static async Task SeedAsync(AppDbContext db)
    {
        var sports = new Dictionary<string, Sport>();
        foreach (var name in new[] { "Gym", "Boxing", "Yoga", "GroupX" })
        {
            sports[name] = await db.Sports.FirstOrDefaultAsync(s => s.Name == name)
                           ?? db.Sports.Add(new Sport { Name = name }).Entity;
        }

        var hash = BCrypt.Net.BCrypt.HashPassword(Password);
        (string Email, string Name, UserRole Role, string? Sport)[] accounts =
        [
            ("manager@sportscenter.demo", "Quản lý Demo", UserRole.Manager, null),
            ("reception@sportscenter.demo", "Lễ tân Demo", UserRole.Receptionist, null),
            ("coach.gym@sportscenter.demo", "HLV Gym", UserRole.Coach, "Gym"),
            ("coach.boxing@sportscenter.demo", "HLV Boxing", UserRole.Coach, "Boxing"),
            ("coach.yoga@sportscenter.demo", "HLV Yoga", UserRole.Coach, "Yoga"),
            ("coach.groupx@sportscenter.demo", "HLV GroupX", UserRole.Coach, "GroupX"),
            ..Enumerable.Range(1, 5).Select(i => ($"member{i}@sportscenter.demo", $"Hội viên {i}", UserRole.Member, (string?)null)),
        ];

        foreach (var (email, name, role, sport) in accounts)
        {
            if (await db.Users.AnyAsync(u => u.Email == email)) continue;
            var user = new User { Email = email, FullName = name, Role = role, PasswordHash = hash, EmailVerified = true };
            if (role == UserRole.Coach) user.CoachProfile = new CoachProfile { Sports = [sports[sport!]] };
            if (role == UserRole.Member) user.MemberProfile = new MemberProfile();
            db.Users.Add(user);
        }

        await db.SaveChangesAsync();
    }
}
