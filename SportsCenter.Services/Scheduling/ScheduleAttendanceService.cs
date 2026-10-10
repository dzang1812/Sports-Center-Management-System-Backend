using System.ComponentModel.DataAnnotations;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;

namespace SportsCenter.Services.Scheduling;

public record PersonalScheduleItem(string Type, int BookingId, int ReferenceId, string Title, string Sport,
    DateOnly Date, TimeOnly StartTime, TimeOnly EndTime, int? RoomId, string? Room, int CoachId, string Coach, BookingStatus Status);
public record AttendanceHistoryItem(string Type, int ReferenceId, string Title, DateOnly Date, AttendanceStatus Status);
public record AttendanceHistoryResponse(int Year, int Month, int CountedSessions, int AttendedSessions, decimal AttendanceRate,
    List<AttendanceHistoryItem> Items, string Formula);
public record CoachScheduleItem(string Type, int ReferenceId, string Title, string Sport, DateOnly Date, TimeOnly StartTime,
    TimeOnly EndTime, int? RoomId, string? Room, int ParticipantCount, SessionStatus Status);
public record RosterItem(int MemberId, string FullName, string Email, int BookingId, AttendanceStatus Status,
    bool AttendanceRecorded = false, bool AttendanceFinalized = false);
public record AttendanceEntry(int MemberId, AttendanceStatus Status = AttendanceStatus.Absent);
public record BulkAttendanceRequest([param: Required, MinLength(1)] List<AttendanceEntry> Items);

public interface IScheduleAttendanceService
{
    Task<List<PersonalScheduleItem>> GetPersonalScheduleAsync(int memberId, DateOnly from, DateOnly to, bool includeCancelled, CancellationToken ct);
    Task<AttendanceHistoryResponse> GetAttendanceHistoryAsync(int memberId, int year, int month, CancellationToken ct);
    Task<List<CoachScheduleItem>> GetCoachScheduleAsync(int coachId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<List<RosterItem>> GetClassRosterAsync(int coachId, int sessionId, CancellationToken ct);
    Task<List<RosterItem>> GetPtRosterAsync(int coachId, int bookingId, CancellationToken ct);
    Task<List<RosterItem>> MarkClassAttendanceAsync(int coachId, int sessionId, BulkAttendanceRequest request, CancellationToken ct);
    Task<List<RosterItem>> MarkPtAttendanceAsync(int coachId, int bookingId, BulkAttendanceRequest request, CancellationToken ct);
}

public sealed class ScheduleAttendanceService(IAttendanceRepository attendances, IClassRepository classes, IScheduleUserRepository users,
    IAuditLogRepository audit, ISchedulingTransaction transaction, IAppClock clock) : IScheduleAttendanceService
{
    public async Task<List<PersonalScheduleItem>> GetPersonalScheduleAsync(int memberId, DateOnly from, DateOnly to, bool includeCancelled, CancellationToken ct)
    {
        ValidateRange(from, to, 180);
        await EnsureMemberAsync(memberId, ct);
        var classBookings = await attendances.GetMemberClassBookingsAsync(memberId, from, to, includeCancelled, ct);
        var ptBookings = await attendances.GetMemberPtBookingsAsync(memberId, from, to, includeCancelled, ct);
        var classItems = classBookings.Select(x => new PersonalScheduleItem("Class", x.Id, x.SessionId, x.Session.ClassName,
            x.Session.Class.Sport.Name, x.Session.Date, x.Session.StartTime, x.Session.EndTime, x.Session.RoomId, x.Session.Room.Name,
            x.Session.CoachId!.Value, x.Session.Coach!.FullName, x.Status));
        var ptItems = ptBookings.Select(x => new PersonalScheduleItem("PT", x.Id, x.Id, "Personal Training", x.Sport.Name,
            x.Date, x.StartTime, x.EndTime, x.RoomId, x.Room == null ? null : x.Room.Name, x.CoachId, x.Coach.FullName, x.Status));
        return classItems.Concat(ptItems).OrderBy(x => x.Date).ThenBy(x => x.StartTime).ToList();
    }

    public async Task<AttendanceHistoryResponse> GetAttendanceHistoryAsync(int memberId, int year, int month, CancellationToken ct)
    {
        if (month is < 1 or > 12 || year is < 2000 or > 2200) throw new SchedulingException("INVALID_MONTH", "Tháng/năm không hợp lệ.");
        await EnsureMemberAsync(memberId, ct);
        var from = new DateOnly(year, month, 1);
        var to = from.AddMonths(1);
        var today = DateOnly.FromDateTime(clock.LocalNow);
        var classRows = await attendances.GetFinalizedClassAttendanceAsync(memberId, from, to, today, ct);
        var ptRows = await attendances.GetFinalizedPtAttendanceAsync(memberId, from, to, today, ct);
        var items = classRows.Where(x => clock.ToUtc(x.Date, x.EndTime) <= clock.UtcNow)
            .Select(x => new AttendanceHistoryItem("Class", x.ReferenceId, x.Title, x.Date, x.Status))
            .Concat(ptRows.Where(x => clock.ToUtc(x.Date, x.EndTime) <= clock.UtcNow)
                .Select(x => new AttendanceHistoryItem("PT", x.ReferenceId, x.Title, x.Date, x.Status))).OrderBy(x => x.Date).ToList();
        var attended = items.Count(x => x.Status is AttendanceStatus.Present or AttendanceStatus.Late);
        var rate = items.Count == 0 ? 0 : Math.Round(attended * 100m / items.Count, 2);
        return new(year, month, items.Count, attended, rate, items,
            "(Present + Late) / sessions with finalized attendance; cancelled and future sessions are excluded");
    }

    public async Task<List<CoachScheduleItem>> GetCoachScheduleAsync(int coachId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        ValidateRange(from, to, 180);
        await EnsureCoachAsync(coachId, ct);
        var classRows = await attendances.GetCoachClassSessionsAsync(coachId, from, to, ct);
        var ptBookings = await attendances.GetCoachPtBookingsAsync(coachId, from, to, ct);
        var classItems = classRows.Select(x => new CoachScheduleItem("Class", x.Session.Id, x.Session.ClassName, x.Sport, x.Session.Date,
            x.Session.StartTime, x.Session.EndTime, x.Session.RoomId, x.Room, x.Participants, x.Session.Status));
        var ptItems = ptBookings.Select(x => new CoachScheduleItem("PT", x.Id, "Personal Training", x.Sport.Name, x.Date, x.StartTime, x.EndTime,
            x.RoomId, x.Room == null ? null : x.Room.Name, x.Status == BookingStatus.Confirmed ? 1 : 0,
            x.Status == BookingStatus.Cancelled ? SessionStatus.Cancelled : x.Status == BookingStatus.Completed ? SessionStatus.Completed : SessionStatus.Scheduled));
        return classItems.Concat(ptItems).OrderBy(x => x.Date).ThenBy(x => x.StartTime).ToList();
    }

    public async Task<List<RosterItem>> GetClassRosterAsync(int coachId, int sessionId, CancellationToken ct)
    {
        await OwnsClassSessionAsync(coachId, sessionId, ct);
        return await ClassRosterAsync(sessionId, ct);
    }

    public async Task<List<RosterItem>> GetPtRosterAsync(int coachId, int bookingId, CancellationToken ct)
    {
        await OwnsPtBookingAsync(coachId, bookingId, ct);
        return await PtRosterAsync(bookingId, ct);
    }

    public async Task<List<RosterItem>> MarkClassAttendanceAsync(int coachId, int sessionId, BulkAttendanceRequest request, CancellationToken ct) =>
        await transaction.ExecuteAsync(async token =>
        {
            await EnsureCoachAsync(coachId, token);
            ValidateEntries(request.Items);
            await transaction.LockAsync([new(ScheduleResourceType.Session, sessionId), new(ScheduleResourceType.Coach, coachId)], token);
            var session = await attendances.FindSessionWithClassAsync(sessionId, token)
                ?? throw SchedulingException.NotFound("SESSION_NOT_FOUND", "Không tìm thấy buổi học.");
            if (session.CoachId != coachId) throw SchedulingException.Forbidden("SESSION_NOT_OWNED", "Huấn luyện viên chỉ được điểm danh buổi của mình.");
            ValidateAttendanceDay(session.Date, session.StartTime, session.Status == SessionStatus.Cancelled);
            var memberIds = request.Items.Select(x => x.MemberId).ToList();
            var validIds = await classes.GetConfirmedMemberIdsAsync(sessionId, token);
            if (validIds.Count != memberIds.Count || validIds.Except(memberIds).Any())
                throw new SchedulingException("ATTENDANCE_INCOMPLETE_ROSTER", "Báo cáo điểm danh phải chứa đúng toàn bộ booking còn hiệu lực.");
            var existing = await attendances.GetClassAttendancesAsync(sessionId, memberIds, token);
            foreach (var entry in request.Items)
            {
                if (existing.TryGetValue(entry.MemberId, out var attendance))
                { attendance.Status = entry.Status; attendance.MarkedById = coachId; attendance.MarkedAt = clock.UtcNow; }
                else attendances.AddAttendance(new Attendance { SessionId = sessionId, MemberId = entry.MemberId, Status = entry.Status, MarkedById = coachId, MarkedAt = clock.UtcNow });
            }
            session.AttendanceFinalizedAt = clock.UtcNow;
            session.AttendanceFinalizedByUserId = coachId;
            audit.AddAudit(coachId, "Attendance.ClassUpsert", nameof(ClassSession), sessionId, request);
            await transaction.SaveChangesAsync(token);
            return await ClassRosterAsync(sessionId, token);
        }, ct);

    public async Task<List<RosterItem>> MarkPtAttendanceAsync(int coachId, int bookingId, BulkAttendanceRequest request, CancellationToken ct) =>
        await transaction.ExecuteAsync(async token =>
        {
            await EnsureCoachAsync(coachId, token);
            ValidateEntries(request.Items);
            await transaction.LockAsync([new(ScheduleResourceType.PtBooking, bookingId), new(ScheduleResourceType.Coach, coachId)], token);
            var booking = await attendances.FindPtBookingAsync(bookingId, token)
                ?? throw SchedulingException.NotFound("PT_BOOKING_NOT_FOUND", "Không tìm thấy booking PT.");
            if (booking.CoachId != coachId) throw SchedulingException.Forbidden("BOOKING_NOT_OWNED", "Huấn luyện viên chỉ được điểm danh lịch PT của mình.");
            ValidateAttendanceDay(booking.Date, booking.StartTime, booking.Status == BookingStatus.Cancelled);
            if (request.Items.Count != 1 || request.Items[0].MemberId != booking.MemberId || booking.Status != BookingStatus.Confirmed)
                throw new SchedulingException("INVALID_ROSTER_MEMBER", "PT attendance phải là hội viên có booking hiệu lực.");
            var entry = request.Items[0];
            var attendance = await attendances.GetPtAttendanceAsync(bookingId, entry.MemberId, token);
            if (attendance is null) attendances.AddAttendance(new Attendance { PtBookingId = bookingId, MemberId = entry.MemberId, Status = entry.Status, MarkedById = coachId, MarkedAt = clock.UtcNow });
            else { attendance.Status = entry.Status; attendance.MarkedById = coachId; attendance.MarkedAt = clock.UtcNow; }
            booking.AttendanceFinalizedAt = clock.UtcNow;
            booking.AttendanceFinalizedByUserId = coachId;
            audit.AddAudit(coachId, "Attendance.PtUpsert", nameof(PtBooking), bookingId, request);
            await transaction.SaveChangesAsync(token);
            return await PtRosterAsync(bookingId, token);
        }, ct);

    private async Task<List<RosterItem>> ClassRosterAsync(int sessionId, CancellationToken ct) =>
        (await attendances.GetClassRosterAsync(sessionId, ct)).Select(ToRosterItem).ToList();

    private async Task<List<RosterItem>> PtRosterAsync(int bookingId, CancellationToken ct) =>
        (await attendances.GetPtRosterAsync(bookingId, ct)).Select(ToRosterItem).ToList();

    private static RosterItem ToRosterItem(RosterRow x) => new(x.MemberId, x.FullName, x.Email, x.BookingId, x.Status, x.Recorded, x.Finalized);

    private async Task OwnsClassSessionAsync(int coachId, int sessionId, CancellationToken ct)
    {
        await EnsureCoachAsync(coachId, ct);
        var owner = await attendances.GetClassSessionCoachAsync(sessionId, ct);
        if (owner is null) throw SchedulingException.NotFound("SESSION_NOT_FOUND", "Không tìm thấy buổi học.");
        if (owner != coachId) throw SchedulingException.Forbidden("SESSION_NOT_OWNED", "Huấn luyện viên chỉ được xem roster của mình.");
    }

    private async Task OwnsPtBookingAsync(int coachId, int bookingId, CancellationToken ct)
    {
        await EnsureCoachAsync(coachId, ct);
        var owner = await attendances.GetPtBookingCoachAsync(bookingId, ct);
        if (owner is null) throw SchedulingException.NotFound("PT_BOOKING_NOT_FOUND", "Không tìm thấy booking PT.");
        if (owner != coachId) throw SchedulingException.Forbidden("BOOKING_NOT_OWNED", "Huấn luyện viên chỉ được xem roster của mình.");
    }

    private void ValidateAttendanceDay(DateOnly date, TimeOnly startTime, bool cancelled)
    {
        if (cancelled) throw SchedulingException.Conflict("SESSION_CANCELLED", "Không thể điểm danh buổi đã hủy.");
        if (date != DateOnly.FromDateTime(clock.LocalNow)) throw SchedulingException.Conflict("ATTENDANCE_WRONG_DAY", "Chỉ được điểm danh trong ngày diễn ra theo giờ Việt Nam.");
        if (clock.ToUtc(date, startTime) > clock.UtcNow)
            throw SchedulingException.Conflict("ATTENDANCE_NOT_STARTED", "Chỉ được chốt điểm danh từ giờ bắt đầu buổi học.");
    }

    private static void ValidateEntries(List<AttendanceEntry> entries)
    {
        if (entries.Count == 0 || entries.Select(x => x.MemberId).Distinct().Count() != entries.Count)
            throw new SchedulingException("INVALID_ATTENDANCE_BATCH", "Danh sách điểm danh phải có phần tử và không trùng hội viên.");
        if (entries.Any(x => x.Status is not (AttendanceStatus.Present or AttendanceStatus.Absent or AttendanceStatus.Late)))
            throw new SchedulingException("INVALID_ATTENDANCE_STATUS", "Chỉ chấp nhận Present, Absent hoặc Late.");
    }

    private static void ValidateRange(DateOnly from, DateOnly to, int maxDays)
    {
        if (to < from || to.DayNumber - from.DayNumber > maxDays) throw new SchedulingException("INVALID_DATE_RANGE", $"Khoảng ngày tối đa {maxDays} ngày.");
    }
    private async Task EnsureMemberAsync(int id, CancellationToken ct)
    {
        if (!await users.IsActiveInRoleAsync(id, UserRole.Member, ct))
            throw SchedulingException.Forbidden("MEMBER_INACTIVE", "Hội viên không hoạt động.");
    }
    private async Task EnsureCoachAsync(int id, CancellationToken ct)
    {
        if (!await users.IsActiveInRoleAsync(id, UserRole.Coach, ct))
            throw SchedulingException.Forbidden("COACH_INACTIVE", "Huấn luyện viên không hoạt động.");
    }
}
