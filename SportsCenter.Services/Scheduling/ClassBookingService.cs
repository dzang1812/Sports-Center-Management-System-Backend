using Microsoft.Extensions.Options;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;

namespace SportsCenter.Services.Scheduling;

public record BookForMemberRequest(int? MemberId);
public record BookingDto(int Id, int SessionId, int MemberId, int PerformedByUserId, int SubscriptionId,
    BookingStatus Status, DateTime CreatedAt, DateTime? CancelledAt, bool IsLateCancellation = false);

public interface IClassBookingService
{
    Task<BookingDto> BookAsync(int actorId, int sessionId, BookForMemberRequest request, CancellationToken ct);
    Task CancelAsync(int actorId, int bookingId, string? reason, CancellationToken ct);
}

public sealed class ClassBookingService(IClassRepository classes, IScheduleConflictRepository conflicts, IScheduleUserRepository users,
    IAuditLogRepository audit, INotificationRepository notifications, ISchedulingTransaction transaction, IAppClock clock,
    IOptions<SchedulingOptions> options) : IClassBookingService
{
    private readonly SchedulingOptions settings = options.Value;
    public async Task<BookingDto> BookAsync(int actorId, int sessionId, BookForMemberRequest request, CancellationToken ct)
    {
        var memberId = await ResolveTargetMemberAsync(actorId, request.MemberId, ct);
        return await transaction.ExecuteAsync(async token =>
        {
            var resources = await classes.GetSessionResourcesAsync(sessionId, token)
                ?? throw SchedulingException.NotFound("SESSION_NOT_FOUND", "Không tìm thấy buổi học.");
            await transaction.LockAsync([new(ScheduleResourceType.Sport, resources.SportId)], token);
            resources = await classes.GetSessionResourcesAsync(sessionId, token)
                ?? throw SchedulingException.NotFound("SESSION_NOT_FOUND", "Session was not found.");
            var locks = new List<ScheduleResourceLock>
            {
                new(ScheduleResourceType.Member, memberId),
                new(ScheduleResourceType.Room, resources.RoomId), new(ScheduleResourceType.Session, sessionId),
            };
            if (resources.CoachId is { } coachId) locks.Add(new(ScheduleResourceType.Coach, coachId));
            await transaction.LockAsync(locks, token);
            var session = await classes.GetSessionForBookingAsync(sessionId, token)
                ?? throw SchedulingException.NotFound("SESSION_NOT_FOUND", "Không tìm thấy buổi học.");
            ValidateBookable(session);
            var eligibleCoach = await users.IsCoachForSportAsync(session.CoachId!.Value, session.Class.SportId, token);
            if (!eligibleCoach) throw SchedulingException.Conflict("COACH_NOT_ELIGIBLE", "Huấn luyện viên không còn đúng chuyên môn.");
            SchedulingRules.EnsureBookingWindow(clock.ToUtc(session.Date, session.StartTime), clock.UtcNow,
                settings.ClassBookingOpensDaysBefore, settings.ClassBookingClosesMinutesBefore);
            var duplicate = await classes.HasConfirmedBookingAsync(sessionId, memberId, token);
            if (duplicate) throw SchedulingException.Conflict("BOOKING_EXISTS", "Bạn đã đặt buổi học này.");
            var booked = await classes.CountConfirmedBookingsAsync(sessionId, token);
            if (booked >= session.Capacity) throw SchedulingException.Conflict("SESSION_FULL", "Buổi học đã hết chỗ.");
            if (await conflicts.HasMemberConflictAsync(memberId, session.Date, session.StartTime, session.EndTime, null, token))
                throw SchedulingException.Conflict("MEMBER_SCHEDULE_CONFLICT", "Hội viên đã có lịch trùng giờ.");
            var subscription = await classes.FindClassSubscriptionAsync(memberId, session.Date, session.Class.SportId, token)
                ?? throw SchedulingException.Conflict("SUBSCRIPTION_NOT_ELIGIBLE", "Không có gói Active đúng bộ môn và còn hiệu lực vào ngày học.");
            var booking = new ClassBooking
            {
                SessionId = sessionId, MemberId = memberId, PerformedByUserId = actorId, SubscriptionId = subscription.Id,
                CreatedAt = clock.UtcNow,
            };
            classes.AddBooking(booking);
            await transaction.SaveChangesAsync(token);
            audit.AddAudit(actorId, "ClassBooking.Create", nameof(ClassBooking), booking.Id,
                new { booking.SessionId, TargetMemberId = memberId, booking.PerformedByUserId, booking.SubscriptionId });
            notifications.AddNotification(memberId, actorId, NotificationType.Booking, "Đặt lớp thành công",
                $"Booking buổi {session.ClassName} ngày {session.Date:yyyy-MM-dd} đã được xác nhận.", $"class-booking:{booking.Id}:created");
            await transaction.SaveChangesAsync(token);
            return ToDto(booking);
        }, ct);
    }

    public async Task CancelAsync(int actorId, int bookingId, string? reason, CancellationToken ct)
    {
        var actor = await GetActiveActorAsync(actorId, ct);
        if (actor.Role is not (UserRole.Member or UserRole.Receptionist))
            throw SchedulingException.Forbidden("ROLE_NOT_ALLOWED", "Chỉ hội viên hoặc lễ tân được hủy booking lớp.");
        await transaction.ExecuteAsync(async token =>
        {
            var snapshot = await classes.GetBookingKeyAsync(bookingId, token)
                ?? throw SchedulingException.NotFound("BOOKING_NOT_FOUND", "Không tìm thấy booking.");
            if (actor.Role == UserRole.Member && snapshot.MemberId != actorId)
                throw SchedulingException.Forbidden("BOOKING_NOT_OWNED", "Không thể hủy booking của hội viên khác.");
            await transaction.LockAsync([new(ScheduleResourceType.Member, snapshot.MemberId), new(ScheduleResourceType.Session, snapshot.SessionId)], token);
            var booking = await classes.GetBookingWithSessionAsync(bookingId, token);
            if (booking.Status == BookingStatus.Cancelled) return 0; // idempotent: repeated DELETE is 204
            if (booking.Status == BookingStatus.Completed) throw SchedulingException.Conflict("BOOKING_COMPLETED", "Không thể hủy booking đã hoàn tất.");
            var startUtc = clock.ToUtc(booking.Session.Date, booking.Session.StartTime);
            if (startUtc <= clock.UtcNow || booking.Session.AttendanceFinalizedAt != null ||
                await classes.HasAttendedAsync(booking.SessionId, booking.MemberId, token))
                throw SchedulingException.Conflict("CLASS_ALREADY_OCCURRED", "Không thể hủy booking đã bắt đầu, đã hoàn tất hoặc đã ghi nhận tham dự.");
            var timely = SchedulingRules.MeetsCancellationCutoff(startUtc, clock.UtcNow, settings.ClassCancellationCutoffHours);
            if (!timely && !settings.AllowLateClassCancellation)
                throw SchedulingException.Conflict("CLASS_CANCELLATION_CUTOFF", $"Phải hủy lớp trước giờ bắt đầu ít nhất {settings.ClassCancellationCutoffHours} giờ.");
            booking.Status = BookingStatus.Cancelled; booking.CancelledAt = clock.UtcNow; booking.CancelledByUserId = actorId;
            booking.CancellationReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            booking.IsLateCancellation = !timely;
            audit.AddAudit(actorId, "ClassBooking.Cancel", nameof(ClassBooking), bookingId,
                new { TargetMemberId = booking.MemberId, PerformedByUserId = actorId, booking.CancellationReason, booking.IsLateCancellation });
            return 0;
        }, ct);
    }

    private void ValidateBookable(ClassSession session)
    {
        if (session.Status != SessionStatus.Scheduled || session.Class.Status != ClassStatus.Open)
            throw SchedulingException.Conflict("SESSION_NOT_BOOKABLE", "Buổi học chưa mở đặt hoặc đã hủy.");
        if (session.Class.Sport.Status != SportStatus.Active || !session.Class.Sport.HasClasses)
            throw SchedulingException.Conflict("SPORT_UNAVAILABLE", "Bộ môn không còn khả dụng.");
        if (session.Room.Status != RoomStatus.Active)
            throw SchedulingException.Conflict("ROOM_UNAVAILABLE", "Phòng không còn khả dụng.");
        if (session.Coach is null || !session.Coach.IsActive)
            throw SchedulingException.Conflict("COACH_UNAVAILABLE", "Buổi học chưa có huấn luyện viên hợp lệ.");
    }

    private async Task<int> ResolveTargetMemberAsync(int actorId, int? requestedMemberId, CancellationToken ct)
    {
        var actor = await GetActiveActorAsync(actorId, ct);
        var target = actor.Role switch
        {
            UserRole.Member when requestedMemberId is null || requestedMemberId == actorId => actorId,
            UserRole.Member => throw SchedulingException.Forbidden("CANNOT_ACT_FOR_MEMBER", "Hội viên không thể thao tác hộ người khác."),
            UserRole.Receptionist when requestedMemberId is not null => requestedMemberId.Value,
            UserRole.Receptionist => throw new SchedulingException("TARGET_MEMBER_REQUIRED", "Lễ tân phải truyền memberId."),
            _ => throw SchedulingException.Forbidden("ROLE_NOT_ALLOWED", "Vai trò không được đặt lớp."),
        };
        var validMember = await users.IsActiveInRoleAsync(target, UserRole.Member, ct);
        if (!validMember) throw SchedulingException.NotFound("MEMBER_NOT_FOUND", "Không tìm thấy hội viên đang hoạt động.");
        return target;
    }

    private async Task<User> GetActiveActorAsync(int actorId, CancellationToken ct)
    {
        var actor = await users.GetUserAsync(actorId, ct)
            ?? throw SchedulingException.Forbidden("ACTOR_NOT_FOUND", "Tài khoản không hợp lệ.");
        if (!actor.IsActive) throw SchedulingException.Forbidden("ACTOR_INACTIVE", "Tài khoản đã bị khóa.");
        return actor;
    }

    private static BookingDto ToDto(ClassBooking x) => new(x.Id, x.SessionId, x.MemberId, x.PerformedByUserId,
        x.SubscriptionId, x.Status, x.CreatedAt, x.CancelledAt, x.IsLateCancellation);
}
