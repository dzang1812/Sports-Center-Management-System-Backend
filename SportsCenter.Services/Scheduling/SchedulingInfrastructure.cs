using System.Text.Json;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;

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
    public int ClassCheckInOpensMinutesBefore { get; set; } = 15;
    public int ClassPresentGraceMinutes { get; set; } = 10;
    public int ClassCheckInCodeLifetimeMinutes { get; set; } = 5;
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

    public static bool Overlaps(DateOnly firstDate, TimeOnly firstStart, TimeOnly firstEnd,
        DateOnly secondDate, TimeOnly secondStart, TimeOnly secondEnd) =>
        firstDate == secondDate && firstStart < secondEnd && secondStart < firstEnd;

    public static bool SubscriptionCovers(SchedulingSubscriptionSnapshot subscription, int sportId, DateOnly activityDate,
        bool needsClass = false, bool needsPt = false) =>
        subscription.Status == SubscriptionStatus.Active &&
        subscription.StartDate <= activityDate && activityDate <= subscription.EndDate &&
        subscription.SportId == sportId &&
        !subscription.HasPendingPayment &&
        (!needsClass || subscription.IncludesClasses) &&
        (!needsPt || subscription.PtSessionCredits > 0 && subscription.RemainingPtSessions > 0);
}

// Write helpers over the repositories so services never touch the DbContext.
public static class SchedulingRecords
{
    public static async Task EnsureActiveActorAsync(this IScheduleUserRepository users, int actorId, UserRole role, CancellationToken ct)
    {
        if (!await users.IsActiveInRoleAsync(actorId, role, ct))
            throw SchedulingException.Forbidden("ACTOR_INACTIVE", "Tài khoản không hợp lệ hoặc đã bị khóa.");
    }

    public static void AddAudit(this IAuditLogRepository audit, int actorId, string action, string entity, int entityId, object details) =>
        audit.Add(new AuditLog
        {
            UserId = actorId,
            Action = action,
            EntityName = entity,
            EntityId = entityId.ToString(),
            Details = JsonSerializer.Serialize(details),
            CreatedAt = DateTime.UtcNow,
        });

    public static void AddNotification(this INotificationRepository notifications, int userId, int? senderId, NotificationType type,
        string title, string body, string deduplicationKey) => notifications.Add(new Notification
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
