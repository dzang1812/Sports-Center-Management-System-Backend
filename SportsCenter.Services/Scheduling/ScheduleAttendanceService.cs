using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;

namespace SportsCenter.Services.Scheduling;

// AttendanceStatus is null while the session is upcoming or hasn't been checked in/marked yet; once the
// session has ended, the FE treats a null/Absent value as "missed" and Present/Late as "attended".
public record PersonalScheduleItem(string Type, int BookingId, int ReferenceId, string Title, string Sport,
    DateOnly Date, TimeOnly StartTime, TimeOnly EndTime, int? RoomId, string? Room, int CoachId, string Coach,
    BookingStatus Status, AttendanceStatus? AttendanceStatus);
// Status is null for a session that hasn't ended yet ("chưa điểm danh"); once it has ended, a null
// Status means the member missed check-in and is presented as Absent.
public record AttendanceHistoryItem(string Type, int ReferenceId, string Title, DateOnly Date, AttendanceStatus? Status, int? ClassId = null);
public record AttendanceHistoryResponse(int Year, int Month, int CountedSessions, int AttendedSessions, decimal AttendanceRate,
    List<AttendanceHistoryItem> Items, string Formula, bool HasData = true);
public record CoachScheduleItem(string Type, int ReferenceId, string Title, string Sport, DateOnly Date, TimeOnly StartTime,
    TimeOnly EndTime, int? RoomId, string? Room, int ParticipantCount, SessionStatus Status,
    AttendanceStatus? CoachAttendanceStatus = null, DateTime? CoachCheckedInAt = null);
public record RosterItem(int MemberId, string FullName, string Email, int BookingId, AttendanceStatus Status,
    bool AttendanceRecorded = false, bool AttendanceFinalized = false, string CheckInState = "NotCheckedIn",
    DateTime? CheckedInAt = null, AttendanceSource? Source = null);
public record AttendanceEntry(int MemberId, AttendanceStatus Status = AttendanceStatus.Absent);
public record BulkAttendanceRequest([param: Required, MinLength(1)] List<AttendanceEntry> Items);
public record ClassCheckInRequest([param: Required, StringLength(64, MinimumLength = 6)] string Code);
public record ClassCheckInCodeDto(int SessionId, string Code, DateTime ExpiresAt, string QrPayload);
public record ClassSelfCheckInResult(string ActorType, int SessionId, int UserId, AttendanceStatus Status,
    DateTime CheckedInAt, bool AlreadyCheckedIn);

public interface IScheduleAttendanceService
{
    Task<List<PersonalScheduleItem>> GetPersonalScheduleAsync(int memberId, DateOnly from, DateOnly to, bool includeCancelled, CancellationToken ct);
    Task<AttendanceHistoryResponse> GetAttendanceHistoryAsync(int memberId, int year, int month, CancellationToken ct);
    Task<List<CoachScheduleItem>> GetCoachScheduleAsync(int coachId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<List<RosterItem>> GetClassRosterAsync(int coachId, int sessionId, CancellationToken ct);
    Task<List<RosterItem>> GetPtRosterAsync(int coachId, int bookingId, CancellationToken ct);
    Task<ClassCheckInCodeDto> IssueClassCheckInCodeAsync(int actorId, int sessionId, CancellationToken ct);
    Task<ClassSelfCheckInResult> MemberCheckInAsync(int memberId, int sessionId, ClassCheckInRequest request, CancellationToken ct);
    Task<ClassSelfCheckInResult> CoachCheckInAsync(int coachId, int sessionId, ClassCheckInRequest request, CancellationToken ct);
    Task<List<RosterItem>> MarkClassAttendanceAsync(int coachId, int sessionId, BulkAttendanceRequest request, CancellationToken ct);
    Task<List<RosterItem>> MarkPtAttendanceAsync(int coachId, int bookingId, BulkAttendanceRequest request, CancellationToken ct);
}

public sealed class ScheduleAttendanceService(IAttendanceRepository attendances, IScheduleUserRepository users,
    ISchedulingSubscriptionRepository subscriptions, IAuditLogRepository audit, ISchedulingTransaction transaction, IAppClock clock,
    IOptions<SchedulingOptions> options) : IScheduleAttendanceService
{
    private readonly SchedulingOptions settings = options.Value;
    public async Task<List<PersonalScheduleItem>> GetPersonalScheduleAsync(int memberId, DateOnly from, DateOnly to, bool includeCancelled, CancellationToken ct)
    {
        ValidateRange(from, to, 180);
        await EnsureMemberAsync(memberId, ct);
        var classBookings = await attendances.GetMemberClassBookingsAsync(memberId, from, to, includeCancelled, ct);
        var ptBookings = await attendances.GetMemberPtBookingsAsync(memberId, from, to, includeCancelled, ct);
        var classAttendance = await attendances.GetClassAttendanceMapAsync(memberId, classBookings.Select(x => x.SessionId).ToList(), ct);
        var ptAttendance = await attendances.GetPtAttendanceMapAsync(memberId, ptBookings.Select(x => x.Id).ToList(), ct);
        var classItems = classBookings.Select(x => new PersonalScheduleItem("Class", x.Id, x.SessionId, x.Session.ClassName,
            x.Session.Class.Sport.Name, x.Session.Date, x.Session.StartTime, x.Session.EndTime, x.Session.RoomId, x.Session.Room.Name,
            x.Session.CoachId!.Value, x.Session.Coach!.FullName, x.Status,
            classAttendance.TryGetValue(x.SessionId, out var cs) ? cs : null));
        var ptItems = ptBookings.Select(x => new PersonalScheduleItem("PT", x.Id, x.Id, "Personal Training", x.Sport.Name,
            x.Date, x.StartTime, x.EndTime, x.RoomId, x.Room == null ? null : x.Room.Name, x.CoachId, x.Coach.FullName, x.Status,
            ptAttendance.TryGetValue(x.Id, out var ps) ? ps : null));
        return classItems.Concat(ptItems).OrderBy(x => x.Date).ThenBy(x => x.StartTime).ToList();
    }

    public async Task<AttendanceHistoryResponse> GetAttendanceHistoryAsync(int memberId, int year, int month, CancellationToken ct)
    {
        if (month is < 1 or > 12 || year is < 2000 or > 2200) throw new SchedulingException("INVALID_MONTH", "Tháng/năm không hợp lệ.");
        await EnsureMemberAsync(memberId, ct);
        var from = new DateOnly(year, month, 1);
        var to = from.AddMonths(1);
        var classRows = await attendances.GetMonthClassAttendanceAsync(memberId, from, to, ct);
        var ptRows = await attendances.GetMonthPtAttendanceAsync(memberId, from, to, ct);

        AttendanceHistoryItem ToItem(AttendedRow x, string type)
        {
            var ended = clock.ToUtc(x.Date, x.EndTime) <= clock.UtcNow;
            return new(type, x.ReferenceId, x.Title, x.Date, ended ? x.Status ?? AttendanceStatus.Absent : null, x.ClassId);
        }

        var items = classRows.Select(x => ToItem(x, "Class"))
            .Concat(ptRows.Select(x => ToItem(x, "PT")))
            .OrderBy(x => x.Date).ToList();
        var ended = items.Where(x => x.Status is not null).ToList();
        var attended = ended.Count(x => x.Status is AttendanceStatus.Present or AttendanceStatus.Late);
        var rate = ended.Count == 0 ? 0 : Math.Round(attended * 100m / ended.Count, 2);
        return new(year, month, ended.Count, attended, rate, items,
            "(Present + Late) / eligible sessions that have ended; cancelled bookings/sessions are excluded, upcoming sessions show as chưa điểm danh",
            ended.Count > 0);
    }

    public async Task<List<CoachScheduleItem>> GetCoachScheduleAsync(int coachId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        ValidateRange(from, to, 180);
        await EnsureCoachAsync(coachId, ct);
        var classRows = await attendances.GetCoachClassSessionsAsync(coachId, from, to, ct);
        var ptBookings = await attendances.GetCoachPtBookingsAsync(coachId, from, to, ct);
        var classItems = classRows.Select(x => new CoachScheduleItem("Class", x.Session.Id, x.Session.ClassName, x.Sport, x.Session.Date,
            x.Session.StartTime, x.Session.EndTime, x.Session.RoomId, x.Room, x.Participants, x.Session.Status,
            x.CoachAttendanceStatus, x.CoachCheckedInAt));
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

    public async Task<ClassCheckInCodeDto> IssueClassCheckInCodeAsync(int actorId, int sessionId, CancellationToken ct) =>
        await transaction.ExecuteAsync(async token =>
        {
            await transaction.LockAsync([new(ScheduleResourceType.Session, sessionId)], token);
            var actor = await users.GetUserAsync(actorId, token)
                ?? throw SchedulingException.Forbidden("ACTOR_INACTIVE", "Tài khoản không hợp lệ.");
            if (!actor.IsActive)
                throw SchedulingException.Forbidden("ACTOR_INACTIVE", "Tài khoản đã bị khóa.");
            var session = await attendances.FindSessionWithClassAsync(sessionId, token)
                ?? throw SchedulingException.NotFound("SESSION_NOT_FOUND", "Không tìm thấy buổi học.");
            if (actor.Role == UserRole.Coach && session.CoachId != actorId)
                throw SchedulingException.Forbidden("SESSION_NOT_OWNED", "Huấn luyện viên chỉ được mở mã cho buổi mình được phân công.");
            if (actor.Role is not (UserRole.Coach or UserRole.Receptionist or UserRole.Manager))
                throw SchedulingException.Forbidden("CHECK_IN_CODE_ISSUER_REQUIRED", "Vai trò không được mở mã check-in lớp.");
            ValidateCheckInWindow(session);

            var code = GenerateCheckInCode();
            var endUtc = clock.ToUtc(session.Date, session.EndTime);
            var expiresAt = new[] { clock.UtcNow.AddMinutes(settings.ClassCheckInCodeLifetimeMinutes), endUtc }.Min();
            session.CheckInCodeHash = HashCode(code);
            session.CheckInCodeIssuedAt = clock.UtcNow;
            session.CheckInCodeExpiresAt = expiresAt;
            session.CheckInCodeIssuedByUserId = actorId;
            audit.AddAudit(actorId, "ClassSession.CheckInCodeIssued", nameof(ClassSession), sessionId,
                new { ExpiresAt = expiresAt });
            return new ClassCheckInCodeDto(sessionId, code, expiresAt,
                JsonSerializer.Serialize(new { type = "ClassSessionCheckIn", sessionId, code }));
        }, ct);

    public async Task<ClassSelfCheckInResult> MemberCheckInAsync(
        int memberId, int sessionId, ClassCheckInRequest request, CancellationToken ct)
    {
        return await transaction.ExecuteAsync(async token =>
        {
            await transaction.LockAsync([
                new(ScheduleResourceType.Member, memberId), new(ScheduleResourceType.Session, sessionId),
            ], token);
            await EnsureMemberAsync(memberId, token);
            var session = await attendances.FindSessionWithClassAsync(sessionId, token)
                ?? throw SchedulingException.NotFound("SESSION_NOT_FOUND", "Không tìm thấy buổi học.");
            ValidateCheckInWindow(session);
            ValidateCheckInCode(session, request.Code);
            var booking = await attendances.FindConfirmedClassBookingAsync(sessionId, memberId, token)
                ?? throw SchedulingException.Forbidden("CLASS_BOOKING_REQUIRED", "Bạn không có booking hiệu lực cho buổi này.");
            _ = (await subscriptions.ListForMemberAndSportAsync(memberId, booking.SportId, token))
                .FirstOrDefault(x => x.Id == booking.SubscriptionId &&
                    SchedulingRules.SubscriptionCovers(x, booking.SportId, session.Date, needsClass: true))
                ?? throw SchedulingException.Conflict("SUBSCRIPTION_NOT_ELIGIBLE", "Gói dùng đặt lớp không còn hiệu lực cho buổi học.");
            var existing = await attendances.GetClassAttendanceAsync(sessionId, memberId, token);
            if (existing is not null)
                return ToCheckInResult("Member", sessionId, memberId, existing.Status, existing.MarkedAt, true);

            var status = ResolveCheckInStatus(session);
            var attendance = new Attendance
            {
                SessionId = sessionId,
                MemberId = memberId,
                Status = status,
                Source = AttendanceSource.MemberSelfCheckIn,
                MarkedById = memberId,
                MarkedAt = clock.UtcNow,
            };
            attendances.AddAttendance(attendance);
            audit.AddAudit(memberId, "Attendance.MemberSelfCheckIn", nameof(ClassSession), sessionId,
                new { booking.BookingId, booking.SubscriptionId, Status = status });
            return ToCheckInResult("Member", sessionId, memberId, status, attendance.MarkedAt, false);
        }, ct);
    }

    public async Task<ClassSelfCheckInResult> CoachCheckInAsync(
        int coachId, int sessionId, ClassCheckInRequest request, CancellationToken ct)
    {
        return await transaction.ExecuteAsync(async token =>
        {
            await transaction.LockAsync([
                new(ScheduleResourceType.Coach, coachId), new(ScheduleResourceType.Session, sessionId),
            ], token);
            await EnsureCoachAsync(coachId, token);
            var session = await attendances.FindSessionWithClassAsync(sessionId, token)
                ?? throw SchedulingException.NotFound("SESSION_NOT_FOUND", "Không tìm thấy buổi học.");
            if (session.CoachId != coachId)
                throw SchedulingException.Forbidden("SESSION_NOT_OWNED", "Huấn luyện viên chỉ được check-in buổi đang phân công cho mình.");
            ValidateCheckInWindow(session);
            ValidateCheckInCode(session, request.Code);
            var existing = await attendances.GetCoachAttendanceAsync(sessionId, coachId, token);
            if (existing is not null)
                return ToCheckInResult("Coach", sessionId, coachId, existing.Status, existing.CheckedInAt, true);

            var status = ResolveCheckInStatus(session);
            var attendance = new CoachSessionAttendance
            {
                SessionId = sessionId,
                CoachId = coachId,
                Status = status,
                CheckedInAt = clock.UtcNow,
            };
            attendances.AddCoachAttendance(attendance);
            audit.AddAudit(coachId, "Attendance.CoachSelfCheckIn", nameof(ClassSession), sessionId,
                new { Status = status });
            return ToCheckInResult("Coach", sessionId, coachId, status, attendance.CheckedInAt, false);
        }, ct);
    }

    public Task<List<RosterItem>> MarkClassAttendanceAsync(
        int coachId, int sessionId, BulkAttendanceRequest request, CancellationToken ct) =>
        Task.FromException<List<RosterItem>>(SchedulingException.Forbidden(
            "CLASS_ATTENDANCE_SELF_CHECKIN_ONLY",
            "Hội viên phải tự check-in; huấn luyện viên chỉ được xem roster."));

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
        (await attendances.GetClassRosterAsync(sessionId, ct)).Select(ToClassRosterItem).ToList();

    private async Task<List<RosterItem>> PtRosterAsync(int bookingId, CancellationToken ct) =>
        (await attendances.GetPtRosterAsync(bookingId, ct)).Select(ToPtRosterItem).ToList();

    private RosterItem ToClassRosterItem(RosterRow x)
    {
        var ended = clock.ToUtc(x.Date, x.EndTime) <= clock.UtcNow;
        var state = x.Status?.ToString() ?? (ended ? AttendanceStatus.Absent.ToString() : "NotCheckedIn");
        return new(x.MemberId, x.FullName, x.Email, x.BookingId, x.Status ?? AttendanceStatus.Absent,
            x.Status is not null, ended, state, x.CheckedInAt, x.Source);
    }

    private static RosterItem ToPtRosterItem(RosterRow x)
    {
        var state = x.Status?.ToString() ?? (x.LegacyFinalized ? AttendanceStatus.Absent.ToString() : "NotCheckedIn");
        return new(x.MemberId, x.FullName, x.Email, x.BookingId, x.Status ?? AttendanceStatus.Absent,
            x.Status is not null, x.LegacyFinalized, state, x.CheckedInAt, x.Source);
    }

    private void ValidateCheckInWindow(ClassSession session)
    {
        if (session.Status == SessionStatus.Cancelled)
            throw SchedulingException.Conflict("SESSION_CANCELLED", "Không thể check-in buổi đã hủy.");
        if (session.Status != SessionStatus.Scheduled)
            throw SchedulingException.Conflict("SESSION_NOT_CHECK_IN_ELIGIBLE", "Buổi học không còn mở check-in.");
        var opensAt = clock.ToUtc(session.Date, session.StartTime).AddMinutes(-settings.ClassCheckInOpensMinutesBefore);
        var closesAt = clock.ToUtc(session.Date, session.EndTime);
        if (clock.UtcNow < opensAt)
            throw SchedulingException.Conflict("CLASS_CHECK_IN_NOT_OPEN", $"Check-in mở trước giờ bắt đầu {settings.ClassCheckInOpensMinutesBefore} phút.");
        if (clock.UtcNow >= closesAt)
            throw SchedulingException.Conflict("CLASS_CHECK_IN_CLOSED", "Thời gian check-in của buổi học đã kết thúc.");
    }

    private void ValidateCheckInCode(ClassSession session, string rawCode)
    {
        if (session.CheckInCodeHash is null || session.CheckInCodeExpiresAt is null)
            throw SchedulingException.Conflict("CLASS_CHECK_IN_CODE_REQUIRED", "Buổi học chưa có mã check-in đang hoạt động.");
        if (session.CheckInCodeExpiresAt <= clock.UtcNow)
            throw SchedulingException.Conflict("CLASS_CHECK_IN_CODE_EXPIRED", "Mã check-in đã hết hạn.");
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(session.CheckInCodeHash), Convert.FromHexString(HashCode(rawCode))))
            throw new SchedulingException("CLASS_CHECK_IN_CODE_INVALID", "Mã check-in không hợp lệ.");
    }

    private AttendanceStatus ResolveCheckInStatus(ClassSession session) =>
        clock.UtcNow <= clock.ToUtc(session.Date, session.StartTime).AddMinutes(settings.ClassPresentGraceMinutes)
            ? AttendanceStatus.Present
            : AttendanceStatus.Late;

    private static ClassSelfCheckInResult ToCheckInResult(string actorType, int sessionId, int userId,
        AttendanceStatus status, DateTime checkedInAt, bool alreadyCheckedIn) =>
        new(actorType, sessionId, userId, status, checkedInAt, alreadyCheckedIn);

    private static string GenerateCheckInCode()
    {
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        return string.Concat(Enumerable.Range(0, 8).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]));
    }

    private static string HashCode(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().ToUpperInvariant())));

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
