using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.API;

// Development only. Idempotent and relative-dated so demo bookings do not expire after a few weeks.
public static class DemoSeeder
{
    private const string Password = "Demo@123";

    public static async Task SeedAsync(AppDbContext db)
    {
        var sportDefinitions = new[]
        {
            (Name: "Gym", HasClasses: false, HasPt: true),
            (Name: "Boxing", HasClasses: false, HasPt: true),
            (Name: "Yoga", HasClasses: true, HasPt: false),
            (Name: "GroupX", HasClasses: true, HasPt: false),
        };
        var sports = new Dictionary<string, Sport>();
        foreach (var definition in sportDefinitions)
        {
            var sport = await db.Sports.FirstOrDefaultAsync(s => s.Name == definition.Name)
                ?? db.Sports.Add(new Sport { Name = definition.Name }).Entity;
            sport.HasClasses = definition.HasClasses;
            sport.HasPt = definition.HasPt;
            sport.Status = SportStatus.Active;
            sports[definition.Name] = sport;
        }
        await db.SaveChangesAsync();

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
            if (role == UserRole.Coach) user.CoachProfile = new CoachProfile { Sports = [sports[sport!]], Bio = $"Huấn luyện viên {sport}" };
            if (role == UserRole.Member) user.MemberProfile = new MemberProfile();
            db.Users.Add(user);
        }
        await db.SaveChangesAsync();

        var activeRoom = await db.Rooms.FirstOrDefaultAsync(x => x.Name == "Studio A")
            ?? db.Rooms.Add(new Room { Name = "Studio A", Capacity = 20 }).Entity;
        activeRoom.Status = RoomStatus.Active; activeRoom.IsActive = true;
        var maintenanceRoom = await db.Rooms.FirstOrDefaultAsync(x => x.Name == "Studio Maintenance")
            ?? db.Rooms.Add(new Room { Name = "Studio Maintenance", Capacity = 10 }).Entity;
        maintenanceRoom.Status = RoomStatus.Maintenance; maintenanceRoom.IsActive = false;
        await db.SaveChangesAsync();

        var plans = new Dictionary<string, MembershipPlan>();
        foreach (var sport in sports.Values)
        {
            var name = sport.HasPt ? $"{sport.Name} PT 10" : $"{sport.Name} Classes";
            var plan = await db.MembershipPlans.FirstOrDefaultAsync(x => x.Name == name)
                ?? db.MembershipPlans.Add(new MembershipPlan { Name = name, DurationDays = sport.HasPt ? 60 : 90, Price = 1_000_000 }).Entity;
            plan.SportId = sport.Id; plan.IncludesClasses = sport.HasClasses; plan.PtSessionCredits = sport.HasPt ? 10 : 0;
            plans[sport.Name] = plan;
        }
        await db.SaveChangesAsync();

        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        var members = await db.Users.Where(x => x.Role == UserRole.Member && x.Email.EndsWith("@sportscenter.demo")).OrderBy(x => x.Id).ToListAsync();
        if (members.Count >= 1)
        {
            await EnsureSubscription(db, members[0].Id, plans["Yoga"], today.AddDays(-7), today.AddDays(90));
            await EnsureSubscription(db, members[0].Id, plans["Gym"], today.AddDays(-7), today.AddDays(53));
        }
        if (members.Count >= 2) await EnsureSubscription(db, members[1].Id, plans["GroupX"], today.AddDays(-7), today.AddDays(90));
        if (members.Count >= 3) await EnsureSubscription(db, members[2].Id, plans["Boxing"], today.AddDays(-70), today.AddDays(-10), SubscriptionStatus.Expired);
        await db.SaveChangesAsync();

        var coaches = await db.Users.Include(x => x.CoachProfile!).ThenInclude(x => x.Sports)
            .Where(x => x.Role == UserRole.Coach && x.Email.EndsWith("@sportscenter.demo")).ToListAsync();
        foreach (var coach in coaches.Where(x => x.CoachProfile!.Sports.Any(s => s.HasPt)))
        {
            var sport = coach.CoachProfile!.Sports.Single();
            if (!await db.CoachAvailabilities.AnyAsync(x => x.CoachId == coach.Id && x.IsActive))
                foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday })
                    db.CoachAvailabilities.Add(new CoachAvailability { CoachId = coach.Id, SportId = sport.Id, DayOfWeek = day, StartTime = new(6, 0), EndTime = new(9, 0) });
        }

        var yogaCoach = coaches.First(x => x.CoachProfile!.Sports.Any(s => s.Name == "Yoga"));
        var demoClass = await db.Classes.FirstOrDefaultAsync(x => x.Name == "Morning Yoga Demo");
        if (demoClass is null)
        {
            demoClass = new Class
            {
                Name = "Morning Yoga Demo", SportId = sports["Yoga"].Id, RoomId = activeRoom.Id, CoachId = yogaCoach.Id,
                Capacity = 10, Level = Level.Beginner, StartDate = today.AddDays(1), EndDate = today.AddDays(60), Status = ClassStatus.Open,
            };
            db.Classes.Add(demoClass);
            await db.SaveChangesAsync();
        }
        for (var date = demoClass.StartDate; date <= demoClass.EndDate; date = date.AddDays(1))
            if (date.DayOfWeek is DayOfWeek.Tuesday or DayOfWeek.Thursday &&
                !await db.ClassSessions.AnyAsync(x => x.ClassId == demoClass.Id && x.Date == date && x.StartTime == new TimeOnly(18, 0)))
                db.ClassSessions.Add(new ClassSession
                {
                    ClassId = demoClass.Id,
                    CoachId = yogaCoach.Id,
                    RoomId = activeRoom.Id,
                    ClassName = demoClass.Name,
                    Level = demoClass.Level,
                    Capacity = demoClass.Capacity,
                    Date = date,
                    StartTime = new(18, 0),
                    EndTime = new(19, 0),
                });
        await db.SaveChangesAsync();
    }

    private static async Task EnsureSubscription(AppDbContext db, int memberId, MembershipPlan plan, DateOnly start, DateOnly end,
        SubscriptionStatus status = SubscriptionStatus.Active)
    {
        if (await db.MemberSubscriptions.AnyAsync(x => x.MemberId == memberId && x.PlanId == plan.Id)) return;
        db.MemberSubscriptions.Add(new MemberSubscription
        {
            MemberId = memberId, PlanId = plan.Id, StartDate = start, EndDate = end, Status = status,
            RemainingPtSessions = plan.PtSessionCredits,
        });
    }
}
