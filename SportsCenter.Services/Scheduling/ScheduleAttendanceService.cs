using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

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

public sealed class ScheduleAttendanceService(AppDbContext db, ISchedulingTransaction transaction, IAppClock clock) : IScheduleAttendanceService
{
    public async Task<List<PersonalScheduleItem>> GetPersonalScheduleAsync(int memberId, DateOnly from, DateOnly to, bool includeCancelled, CancellationToken ct)
    {
        ValidateRange(from, to, 180);
        await EnsureMemberAsync(memberId, ct);
        var classQuery = db.ClassBookings.AsNoTracking().Where(x => x.MemberId == memberId && x.Session.Date >= from && x.Session.Date <= to);
        var ptQuery = db.PtBookings.AsNoTracking().Where(x => x.MemberId == memberId && x.Date >= from && x.Date <= to);
        if (!includeCancelled)
        {
            classQuery = classQuery.Where(x => x.Status != BookingStatus.Cancelled);
            ptQuery = ptQuery.Where(x => x.Status != BookingStatus.Cancelled);
        }
        var classes = await classQuery.Select(x => new PersonalScheduleItem("Class", x.Id, x.SessionId, x.Session.ClassName,
            x.Session.Class.Sport.Name, x.Session.Date, x.Session.StartTime, x.Session.EndTime, x.Session.RoomId, x.Session.Room.Name,
            x.Session.CoachId!.Value, x.Session.Coach!.FullName, x.Status)).ToListAsync(ct);
        var pts = await ptQuery.Select(x => new PersonalScheduleItem("PT", x.Id, x.Id, "Personal Training", x.Sport.Name,
            x.Date, x.StartTime, x.EndTime, x.RoomId, x.Room == null ? null : x.Room.Name, x.CoachId, x.Coach.FullName, x.Status)).ToListAsync(ct);
        return classes.Concat(pts).OrderBy(x => x.Date).ThenBy(x => x.StartTime).ToList();
    }

    public async Task<AttendanceHistoryResponse> GetAttendanceHistoryAsync(int memberId, int year, int month, CancellationToken ct)
    {
        if (month is < 1 or > 12 || year is < 2000 or > 2200) throw new SchedulingException("INVALID_MONTH", "Tháng/năm không hợp lệ.");
        await EnsureMemberAsync(memberId, ct);
        var from = new DateOnly(year, month, 1);
        var to = from.AddMonths(1);
        var today = DateOnly.FromDateTime(clock.LocalNow);
        var classRows = await db.ClassBookings.AsNoTracking().Where(x => x.MemberId == memberId && x.Status == BookingStatus.Confirmed &&
                x.Session.Status != SessionStatus.Cancelled && x.Session.AttendanceFinalizedAt != null &&
                x.Session.Date >= from && x.Session.Date < to && x.Session.Date <= today)
            .Select(x => new { Item = new AttendanceHistoryItem("Class", x.SessionId, x.Session.ClassName, x.Session.Date,
                db.Attendances.Where(a => a.SessionId == x.SessionId && a.MemberId == memberId).Select(a => a.Status).Single()), x.Session.EndTime }).ToListAsync(ct);
        var ptRows = await db.PtBookings.AsNoTracking().Where(x => x.MemberId == memberId && x.Status != BookingStatus.Cancelled &&
                x.AttendanceFinalizedAt != null && x.Date >= from && x.Date < to && x.Date <= today)
            .Select(x => new { Item = new AttendanceHistoryItem("PT", x.Id, "Personal Training", x.Date,
                db.Attendances.Where(a => a.PtBookingId == x.Id && a.MemberId == memberId).Select(a => a.Status).Single()), x.EndTime }).ToListAsync(ct);
        var items = classRows.Where(x => clock.ToUtc(x.Item.Date, x.EndTime) <= clock.UtcNow).Select(x => x.Item)
            .Concat(ptRows.Where(x => clock.ToUtc(x.Item.Date, x.EndTime) <= clock.UtcNow).Select(x => x.Item)).OrderBy(x => x.Date).ToList();
        var attended = items.Count(x => x.Status is AttendanceStatus.Present or AttendanceStatus.Late);
        var rate = items.Count == 0 ? 0 : Math.Round(attended * 100m / items.Count, 2);
        return new(year, month, items.Count, attended, rate, items,
            "(Present + Late) / sessions with finalized attendance; cancelled and future sessions are excluded");
    }

    public async Task<List<CoachScheduleItem>> GetCoachScheduleAsync(int coachId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        ValidateRange(from, to, 180);
        await EnsureCoachAsync(coachId, ct);
        var classes = await db.ClassSessions.AsNoTracking().Where(x => x.CoachId == coachId && x.Date >= from && x.Date <= to)
            .Select(x => new CoachScheduleItem("Class", x.Id, x.ClassName, x.Class.Sport.Name, x.Date, x.StartTime, x.EndTime,
                x.RoomId, x.Room.Name, db.ClassBookings.Count(b => b.SessionId == x.Id && b.Status == BookingStatus.Confirmed), x.Status)).ToListAsync(ct);
        var pts = await db.PtBookings.AsNoTracking().Where(x => x.CoachId == coachId && x.Date >= from && x.Date <= to)
            .Select(x => new CoachScheduleItem("PT", x.Id, "Personal Training", x.Sport.Name, x.Date, x.StartTime, x.EndTime,
                x.RoomId, x.Room == null ? null : x.Room.Name, x.Status == BookingStatus.Confirmed ? 1 : 0,
                x.Status == BookingStatus.Cancelled ? SessionStatus.Cancelled : x.Status == BookingStatus.Completed ? SessionStatus.Completed : SessionStatus.Scheduled)).ToListAsync(ct);
        return classes.Concat(pts).OrderBy(x => x.Date).ThenBy(x => x.StartTime).ToList();
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
            var session = await db.ClassSessions.Include(x => x.Class).FirstOrDefaultAsync(x => x.Id == sessionId, token)
                ?? throw SchedulingException.NotFound("SESSION_NOT_FOUND", "Không tìm thấy buổi học.");
            if (session.CoachId != coachId) throw SchedulingException.Forbidden("SESSION_NOT_OWNED", "Huấn luyện viên chỉ được điểm danh buổi của mình.");
            ValidateAttendanceDay(session.Date, session.StartTime, session.Status == SessionStatus.Cancelled);
            var memberIds = request.Items.Select(x => x.MemberId).ToList();
            var validIds = await db.ClassBookings.Where(x => x.SessionId == sessionId && x.Status == BookingStatus.Confirmed)
                .Select(x => x.MemberId).ToListAsync(token);
            if (validIds.Count != memberIds.Count || validIds.Except(memberIds).Any())
                throw new SchedulingException("ATTENDANCE_INCOMPLETE_ROSTER", "Báo cáo điểm danh phải chứa đúng toàn bộ booking còn hiệu lực.");
            var existing = await db.Attendances.Where(x => x.SessionId == sessionId && memberIds.Contains(x.MemberId)).ToDictionaryAsync(x => x.MemberId, token);
            foreach (var entry in request.Items)
            {
                if (existing.TryGetValue(entry.MemberId, out var attendance))
                { attendance.Status = entry.Status; attendance.MarkedById = coachId; attendance.MarkedAt = clock.UtcNow; }
                else db.Attendances.Add(new Attendance { SessionId = sessionId, MemberId = entry.MemberId, Status = entry.Status, MarkedById = coachId, MarkedAt = clock.UtcNow });
            }
            session.AttendanceFinalizedAt = clock.UtcNow;
            session.AttendanceFinalizedByUserId = coachId;
            SchedulingRules.AddAudit(db, coachId, "Attendance.ClassUpsert", nameof(ClassSession), sessionId, request);
            await db.SaveChangesAsync(token);
            return await ClassRosterAsync(sessionId, token);
        }, ct);

    public async Task<List<RosterItem>> MarkPtAttendanceAsync(int coachId, int bookingId, BulkAttendanceRequest request, CancellationToken ct) =>
        await transaction.ExecuteAsync(async token =>
        {
            await EnsureCoachAsync(coachId, token);
            ValidateEntries(request.Items);
            await transaction.LockAsync([new(ScheduleResourceType.PtBooking, bookingId), new(ScheduleResourceType.Coach, coachId)], token);
            var booking = await db.PtBookings.FirstOrDefaultAsync(x => x.Id == bookingId, token)
                ?? throw SchedulingException.NotFound("PT_BOOKING_NOT_FOUND", "Không tìm thấy booking PT.");
            if (booking.CoachId != coachId) throw SchedulingException.Forbidden("BOOKING_NOT_OWNED", "Huấn luyện viên chỉ được điểm danh lịch PT của mình.");
            ValidateAttendanceDay(booking.Date, booking.StartTime, booking.Status == BookingStatus.Cancelled);
            if (request.Items.Count != 1 || request.Items[0].MemberId != booking.MemberId || booking.Status != BookingStatus.Confirmed)
                throw new SchedulingException("INVALID_ROSTER_MEMBER", "PT attendance phải là hội viên có booking hiệu lực.");
            var entry = request.Items[0];
            var attendance = await db.Attendances.FirstOrDefaultAsync(x => x.PtBookingId == bookingId && x.MemberId == entry.MemberId, token);
            if (attendance is null) db.Attendances.Add(new Attendance { PtBookingId = bookingId, MemberId = entry.MemberId, Status = entry.Status, MarkedById = coachId, MarkedAt = clock.UtcNow });
            else { attendance.Status = entry.Status; attendance.MarkedById = coachId; attendance.MarkedAt = clock.UtcNow; }
            booking.AttendanceFinalizedAt = clock.UtcNow;
            booking.AttendanceFinalizedByUserId = coachId;
            SchedulingRules.AddAudit(db, coachId, "Attendance.PtUpsert", nameof(PtBooking), bookingId, request);
            await db.SaveChangesAsync(token);
            return await PtRosterAsync(bookingId, token);
        }, ct);

    private Task<List<RosterItem>> ClassRosterAsync(int sessionId, CancellationToken ct) => db.ClassBookings.AsNoTracking()
        .Where(x => x.SessionId == sessionId && x.Status == BookingStatus.Confirmed)
        .OrderBy(x => x.Member.FullName).Select(x => new RosterItem(x.MemberId, x.Member.FullName, x.Member.Email, x.Id,
            db.Attendances.Where(a => a.SessionId == sessionId && a.MemberId == x.MemberId).Select(a => (AttendanceStatus?)a.Status).FirstOrDefault() ?? AttendanceStatus.Absent,
            db.Attendances.Any(a => a.SessionId == sessionId && a.MemberId == x.MemberId), x.Session.AttendanceFinalizedAt != null)).ToListAsync(ct);

    private Task<List<RosterItem>> PtRosterAsync(int bookingId, CancellationToken ct) => db.PtBookings.AsNoTracking().Where(x => x.Id == bookingId && x.Status == BookingStatus.Confirmed)
        .Select(x => new RosterItem(x.MemberId, x.Member.FullName, x.Member.Email, x.Id,
            db.Attendances.Where(a => a.PtBookingId == bookingId && a.MemberId == x.MemberId).Select(a => (AttendanceStatus?)a.Status).FirstOrDefault() ?? AttendanceStatus.Absent,
            db.Attendances.Any(a => a.PtBookingId == bookingId && a.MemberId == x.MemberId), x.AttendanceFinalizedAt != null)).ToListAsync(ct);

    private async Task OwnsClassSessionAsync(int coachId, int sessionId, CancellationToken ct)
    {
        await EnsureCoachAsync(coachId, ct);
        var owner = await db.ClassSessions.Where(x => x.Id == sessionId).Select(x => x.CoachId).FirstOrDefaultAsync(ct);
        if (owner is null) throw SchedulingException.NotFound("SESSION_NOT_FOUND", "Không tìm thấy buổi học.");
        if (owner != coachId) throw SchedulingException.Forbidden("SESSION_NOT_OWNED", "Huấn luyện viên chỉ được xem roster của mình.");
    }

    private async Task OwnsPtBookingAsync(int coachId, int bookingId, CancellationToken ct)
    {
        await EnsureCoachAsync(coachId, ct);
        var owner = await db.PtBookings.Where(x => x.Id == bookingId).Select(x => (int?)x.CoachId).FirstOrDefaultAsync(ct);
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
        if (!await db.Users.AnyAsync(x => x.Id == id && x.Role == UserRole.Member && x.IsActive, ct))
            throw SchedulingException.Forbidden("MEMBER_INACTIVE", "Hội viên không hoạt động.");
    }
    private async Task EnsureCoachAsync(int id, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(x => x.Id == id && x.Role == UserRole.Coach && x.IsActive, ct))
            throw SchedulingException.Forbidden("COACH_INACTIVE", "Huấn luyện viên không hoạt động.");
    }
}
