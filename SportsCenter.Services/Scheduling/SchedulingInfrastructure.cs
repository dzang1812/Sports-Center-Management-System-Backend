using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Services.Scheduling;

public sealed class SchedulingOptions
{
    public const string SectionName = "Scheduling";
    public int ClassCancellationCutoffHours { get; set; } = 12;
    public int PtCancellationCutoffHours { get; set; } = 12;
    public bool AllowLateClassCancellation { get; set; } = true;
    public bool AllowLatePtCancellation { get; set; } = true;
    public int? ClassBookingOpensDaysBefore { get; set; }
    public int ClassBookingClosesMinutesBefore { get; set; }
    public int? PtBookingOpensDaysBefore { get; set; }
    public int PtBookingClosesMinutesBefore { get; set; }
}

public sealed class SchedulingException(string errorCode, string message, int statusCode = 400, object? details = null) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
    public int StatusCode { get; } = statusCode;
    public object? Details { get; } = details;

    public static SchedulingException NotFound(string code, string message) => new(code, message, 404);
    public static SchedulingException Forbidden(string code, string message) => new(code, message, 403);
    public static SchedulingException Conflict(string code, string message) => new(code, message, 409);
    public static SchedulingException Conflict(string code, string message, object details) => new(code, message, 409, details);
}

public interface IAppClock
{
    DateTime UtcNow { get; }
    DateTime LocalNow { get; }
    DateTime ToUtc(DateOnly date, TimeOnly time);
}

public sealed class VietnamClock : IAppClock
{
    private static readonly TimeZoneInfo Zone = FindZone();
    public DateTime UtcNow => DateTime.UtcNow;
    public DateTime LocalNow => TimeZoneInfo.ConvertTimeFromUtc(UtcNow, Zone);

    public DateTime ToUtc(DateOnly date, TimeOnly time) => TimeZoneInfo.ConvertTimeToUtc(
        DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified), Zone);

    private static TimeZoneInfo FindZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); }
    }
}

public enum ScheduleResourceType : long { Sport = 1, Member = 2, Coach = 3, Room = 4, Class = 5, Session = 6, PtBooking = 7 }
public readonly record struct ScheduleResourceLock(ScheduleResourceType Type, int Id)
{
    public long Key => (long)Type * 10_000_000_000L + Id;
}

public interface ISchedulingTransaction
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default);
    Task LockAsync(IEnumerable<ScheduleResourceLock> locks, CancellationToken cancellationToken = default);
}

public sealed class SchedulingTransaction(AppDbContext db) : ISchedulingTransaction
{
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        // READ COMMITTED deliberately pairs with transaction-scoped advisory locks: statements
        // waiting on a resource lock must see the commit that released it, not an older snapshot.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            var result = await work(cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception ex) when (IsConcurrency(ex))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw SchedulingException.Conflict("SCHEDULE_CONFLICT", "Dữ liệu lịch vừa thay đổi. Vui lòng tải lại và thử lại.");
        }
    }

    public async Task LockAsync(IEnumerable<ScheduleResourceLock> locks, CancellationToken cancellationToken = default)
    {
        foreach (var key in locks.Select(x => x.Key).Distinct().Order())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken);
    }

    private static bool IsConcurrency(Exception ex) => ex is DbUpdateConcurrencyException ||
        ex is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.UniqueViolation } ||
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.UniqueViolation };
}

public static class SchedulingRules
{
    public static bool MeetsCancellationCutoff(DateTime startUtc, DateTime nowUtc, int cutoffHours) =>
        startUtc - nowUtc >= TimeSpan.FromHours(cutoffHours);

    public static void EnsureBookingWindow(DateTime startUtc, DateTime nowUtc, int? opensDaysBefore, int closesMinutesBefore)
    {
        if (startUtc <= nowUtc.AddMinutes(closesMinutesBefore))
            throw SchedulingException.Conflict("BOOKING_CLOSED", "Thoi gian dat lich da dong.");
        if (opensDaysBefore is { } days && startUtc > nowUtc.AddDays(days))
            throw SchedulingException.Conflict("BOOKING_NOT_OPEN", "Chua den thoi gian duoc phep dat lich.");
    }

    public static async Task EnsureActiveActorAsync(AppDbContext db, int actorId, UserRole role, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(x => x.Id == actorId && x.Role == role && x.IsActive, ct))
            throw SchedulingException.Forbidden("ACTOR_INACTIVE", "Tài khoản không hợp lệ hoặc đã bị khóa.");
    }

    public static bool Overlaps(DateOnly firstDate, TimeOnly firstStart, TimeOnly firstEnd,
        DateOnly secondDate, TimeOnly secondStart, TimeOnly secondEnd) =>
        firstDate == secondDate && firstStart < secondEnd && secondStart < firstEnd;

    public static bool SubscriptionCovers(MemberSubscription subscription, int sportId, DateOnly activityDate,
        bool needsClass = false, bool needsPt = false) =>
        subscription.Status == SubscriptionStatus.Active &&
        subscription.StartDate <= activityDate && activityDate <= subscription.EndDate &&
        subscription.Plan.SportId == sportId &&
        (!needsClass || subscription.Plan.IncludesClasses) &&
        (!needsPt || subscription.Plan.PtSessionCredits > 0);

    public static void AddAudit(AppDbContext db, int actorId, string action, string entity, int entityId, object details) =>
        db.AuditLogs.Add(new AuditLog
        {
            UserId = actorId,
            Action = action,
            EntityName = entity,
            EntityId = entityId.ToString(),
            Details = JsonSerializer.Serialize(details),
            CreatedAt = DateTime.UtcNow,
        });

    public static void AddNotification(AppDbContext db, int userId, int? senderId, NotificationType type,
        string title, string body, string deduplicationKey) => db.Notifications.Add(new Notification
        {
            UserId = userId,
            SenderId = senderId,
            Type = type,
            Title = title,
            Body = body,
            DeduplicationKey = deduplicationKey,
            CreatedAt = DateTime.UtcNow,
        });
}
