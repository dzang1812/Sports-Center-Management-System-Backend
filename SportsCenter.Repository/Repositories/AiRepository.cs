using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

public record AiExerciseRow(int Id, string Name, string? MuscleGroup, Level Level);
public record AiPlanRow(int Id, string Name, string? Description, decimal Price, int DurationDays, string? Sport, bool IncludesClasses, bool IncludesGymAccess, int PtSessionCredits, bool IsPtPackage);
public record AiCoachRow(int Id, string Name, string? Bio, string? Certifications, List<string> Sports);
public record AiRoomRow(int Id, string Name, int Capacity);
public record AiClassRow(int Id, string Name, string Sport, string Room, string? Coach, Level Level, DateOnly StartDate, DateOnly EndDate, decimal Fee, int Capacity);
public record AiCenterInfo(List<AiPlanRow> Plans, List<AiCoachRow> Coaches, List<AiRoomRow> Rooms, List<AiClassRow> Classes);

public interface IAiRepository
{
    // Chat history
    Task<List<AiChatMessage>> RecentMessagesAsync(int userId, int take, CancellationToken ct);
    Task<List<AiChatMessage>> HistoryAsync(int userId, CancellationToken ct);
    Task AddMessagesAsync(IEnumerable<AiChatMessage> messages, CancellationToken ct);
    Task ClearHistoryAsync(int userId, CancellationToken ct);

    // Usage control
    Task<bool> HasActiveSubscriptionAsync(int userId, DateOnly today, CancellationToken ct);
    Task<AiMonthlyBudget?> GetBudgetAsync(string period, CancellationToken ct);
    Task AddSpendAsync(string period, decimal amountUsd, CancellationToken ct);
    Task<AiDailyQuota?> GetQuotaAsync(int userId, string date, CancellationToken ct);
    Task IncrementQuotaAsync(int userId, string date, string counter, CancellationToken ct);
    Task LogUsageAsync(AiUsageLog log, CancellationToken ct);
    Task LogFeatureUsageAsync(AiFeatureUsageLog log, CancellationToken ct);

    // Member context + plans
    Task<User?> GetMemberAsync(int userId, CancellationToken ct);
    Task<AiCenterInfo> CenterInfoAsync(DateOnly today, CancellationToken ct);
    Task<List<AiExerciseRow>> ExercisesAsync(CancellationToken ct);
    Task<Exercise?> GetExerciseAsync(int id, CancellationToken ct);
    Task<TrainingPlan?> GetActivePlanAsync(int memberId, CancellationToken ct);
    Task<TrainingPlan?> GetOwnedPlanAsync(int memberId, int planId, CancellationToken ct);
    Task DeactivatePlansAsync(int memberId, CancellationToken ct);
    void AddPlan(TrainingPlan plan);

    // Session evaluation
    Task<ClassSession?> GetSessionAsync(int sessionId, CancellationToken ct);
    Task<List<TrainingResult>> ResultsAsync(int memberId, int sessionId, CancellationToken ct);
    Task<List<TrainingResult>> PastResultsAsync(int memberId, int excludeSessionId, int take, CancellationToken ct);
    Task<AiWorkoutEvaluation?> GetEvaluationAsync(int memberId, int sessionId, CancellationToken ct);
    Task AddEvaluationAsync(AiWorkoutEvaluation evaluation, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

public class AiRepository(AppDbContext db) : IAiRepository
{
    public Task<List<AiChatMessage>> RecentMessagesAsync(int userId, int take, CancellationToken ct) =>
        db.AiChatMessages.AsNoTracking().Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(take).ToListAsync(ct);

    public Task<List<AiChatMessage>> HistoryAsync(int userId, CancellationToken ct) =>
        db.AiChatMessages.AsNoTracking().Where(x => x.UserId == userId)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(ct);

    public async Task AddMessagesAsync(IEnumerable<AiChatMessage> messages, CancellationToken ct)
    {
        db.AiChatMessages.AddRange(messages);
        await db.SaveChangesAsync(ct);
    }

    public Task ClearHistoryAsync(int userId, CancellationToken ct) =>
        db.AiChatMessages.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);

    public Task<bool> HasActiveSubscriptionAsync(int userId, DateOnly today, CancellationToken ct) =>
        db.MemberSubscriptions.AnyAsync(x => x.MemberId == userId && x.Status == SubscriptionStatus.Active
            && x.StartDate <= today && x.EndDate >= today, ct);

    public Task<AiMonthlyBudget?> GetBudgetAsync(string period, CancellationToken ct) =>
        db.AiMonthlyBudgets.AsNoTracking().FirstOrDefaultAsync(x => x.Period == period, ct);

    // Single-statement upsert: concurrent calls can't lose an increment.
    public Task AddSpendAsync(string period, decimal amountUsd, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AiMonthlyBudgets" ("Period", "SpentUsd", "UpdatedAt")
            VALUES ({period}, {amountUsd}, now())
            ON CONFLICT ("Period") DO UPDATE
            SET "SpentUsd" = "AiMonthlyBudgets"."SpentUsd" + {amountUsd}, "UpdatedAt" = now()
            """, ct);

    public Task<AiDailyQuota?> GetQuotaAsync(int userId, string date, CancellationToken ct) =>
        db.AiDailyQuotas.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId && x.Date == date, ct);

    public Task IncrementQuotaAsync(int userId, string date, string counter, CancellationToken ct) => counter switch
    {
        "Chat" => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AiDailyQuotas" ("UserId", "Date", "ChatCount", "GenerateCount", "AnalyzeCount", "EvaluateCount")
            VALUES ({userId}, {date}, 1, 0, 0, 0)
            ON CONFLICT ("UserId", "Date") DO UPDATE SET "ChatCount" = "AiDailyQuotas"."ChatCount" + 1
            """, ct),
        "Generate" => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AiDailyQuotas" ("UserId", "Date", "ChatCount", "GenerateCount", "AnalyzeCount", "EvaluateCount")
            VALUES ({userId}, {date}, 0, 1, 0, 0)
            ON CONFLICT ("UserId", "Date") DO UPDATE SET "GenerateCount" = "AiDailyQuotas"."GenerateCount" + 1
            """, ct),
        "Evaluate" => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AiDailyQuotas" ("UserId", "Date", "ChatCount", "GenerateCount", "AnalyzeCount", "EvaluateCount")
            VALUES ({userId}, {date}, 0, 0, 0, 1)
            ON CONFLICT ("UserId", "Date") DO UPDATE SET "EvaluateCount" = "AiDailyQuotas"."EvaluateCount" + 1
            """, ct),
        _ => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AiDailyQuotas" ("UserId", "Date", "ChatCount", "GenerateCount", "AnalyzeCount", "EvaluateCount")
            VALUES ({userId}, {date}, 0, 0, 1, 0)
            ON CONFLICT ("UserId", "Date") DO UPDATE SET "AnalyzeCount" = "AiDailyQuotas"."AnalyzeCount" + 1
            """, ct),
    };

    public async Task LogUsageAsync(AiUsageLog log, CancellationToken ct)
    {
        db.AiUsageLogs.Add(log);
        await db.SaveChangesAsync(ct);
    }

    public async Task LogFeatureUsageAsync(AiFeatureUsageLog log, CancellationToken ct)
    {
        db.AiFeatureUsageLogs.Add(log);
        await db.SaveChangesAsync(ct);
    }

    // Read-only snapshot of what the center sells, so the AI can recommend real plans, PTs, rooms and classes.
    public async Task<AiCenterInfo> CenterInfoAsync(DateOnly today, CancellationToken ct)
    {
        var plans = await db.MembershipPlans.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Price)
            .Select(p => new AiPlanRow(p.Id, p.Name, p.Description, p.Price, p.DurationDays, p.Sport != null ? p.Sport.Name : null,
                p.IncludesClasses, p.IncludesGymAccess, p.PtSessionCredits, p.WalletPtPackage)).ToListAsync(ct);
        var coaches = await db.Users.AsNoTracking().Where(u => u.Role == UserRole.Coach && u.IsActive && u.CoachProfile != null)
            .Select(u => new { u.Id, u.FullName, u.CoachProfile!.Bio, u.CoachProfile.Certifications, Sports = u.CoachProfile.Sports.Select(s => s.Name).ToList() })
            .ToListAsync(ct);
        var rooms = await db.Rooms.AsNoTracking().Where(r => r.Status == RoomStatus.Active)
            .Select(r => new AiRoomRow(r.Id, r.Name, r.Capacity)).ToListAsync(ct);
        var classes = await db.Classes.AsNoTracking().Where(c => c.Status == ClassStatus.Open && c.EndDate >= today)
            .OrderBy(c => c.StartDate).Take(30)
            .Select(c => new AiClassRow(c.Id, c.Name, c.Sport.Name, c.Room.Name, c.Coach != null ? c.Coach.FullName : null,
                c.Level, c.StartDate, c.EndDate, c.CourseFee, c.Capacity)).ToListAsync(ct);
        return new AiCenterInfo(plans, coaches.Select(c => new AiCoachRow(c.Id, c.FullName, c.Bio, c.Certifications, c.Sports)).ToList(), rooms, classes);
    }

    public Task<User?> GetMemberAsync(int userId, CancellationToken ct) =>
        db.Users.AsNoTracking().Include(x => x.MemberProfile).FirstOrDefaultAsync(x => x.Id == userId, ct);

    public Task<List<AiExerciseRow>> ExercisesAsync(CancellationToken ct) =>
        db.Exercises.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new AiExerciseRow(x.Id, x.Name, x.MuscleGroup, x.Level)).ToListAsync(ct);

    public Task<Exercise?> GetExerciseAsync(int id, CancellationToken ct) =>
        db.Exercises.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);

    public Task<TrainingPlan?> GetActivePlanAsync(int memberId, CancellationToken ct) =>
        db.TrainingPlans.AsNoTracking().AsSplitQuery()
            .Include(x => x.Sessions).Include(x => x.Items).ThenInclude(i => i.Exercise)
            .Where(x => x.MemberId == memberId && x.IsActive)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);

    public Task<TrainingPlan?> GetOwnedPlanAsync(int memberId, int planId, CancellationToken ct) =>
        db.TrainingPlans.AsSplitQuery()
            .Include(x => x.Sessions).Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == planId && x.MemberId == memberId, ct);

    public Task DeactivatePlansAsync(int memberId, CancellationToken ct) =>
        db.TrainingPlans.Where(x => x.MemberId == memberId && x.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), ct);

    public void AddPlan(TrainingPlan plan) => db.TrainingPlans.Add(plan);

    public Task<ClassSession?> GetSessionAsync(int sessionId, CancellationToken ct) =>
        db.ClassSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sessionId, ct);

    public Task<List<TrainingResult>> ResultsAsync(int memberId, int sessionId, CancellationToken ct) =>
        db.TrainingResults.AsNoTracking().Include(x => x.Exercise)
            .Where(x => x.MemberId == memberId && x.SessionId == sessionId).OrderBy(x => x.Id).ToListAsync(ct);

    public Task<List<TrainingResult>> PastResultsAsync(int memberId, int excludeSessionId, int take, CancellationToken ct) =>
        db.TrainingResults.AsNoTracking().Include(x => x.Exercise).Include(x => x.Session)
            .Where(x => x.MemberId == memberId && x.SessionId != null && x.SessionId != excludeSessionId)
            .OrderByDescending(x => x.Session!.Date).ThenByDescending(x => x.Id).Take(take).ToListAsync(ct);

    public Task<AiWorkoutEvaluation?> GetEvaluationAsync(int memberId, int sessionId, CancellationToken ct) =>
        db.AiWorkoutEvaluations.AsNoTracking().FirstOrDefaultAsync(x => x.MemberId == memberId && x.SessionId == sessionId, ct);

    public async Task AddEvaluationAsync(AiWorkoutEvaluation evaluation, CancellationToken ct)
    {
        db.AiWorkoutEvaluations.Add(evaluation);
        await db.SaveChangesAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
