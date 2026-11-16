using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;

namespace SportsCenter.Services.Scheduling;

// Trạng thái tham dự chưa có khi buổi chưa diễn ra hoặc chưa được điểm danh; sau khi kết thúc,
// giao diện tính chưa điểm danh hoặc vắng là bỏ lỡ, có mặt hoặc đến muộn là đã tham dự.
public record PersonalScheduleItem(string Type, int BookingId, int ReferenceId, string Title, string Sport,
    DateOnly Date, TimeOnly StartTime, TimeOnly EndTime, int? RoomId, string? Room, int CoachId, string Coach,
    BookingStatus Status, AttendanceStatus? AttendanceStatus, int? EnrollmentId = null, PtFundingSource? FundingSource = null);
// Trạng thái chưa có khi buổi chưa kết thúc nghĩa là chưa điểm danh;
// sau khi kết thúc, chưa có trạng thái được hiển thị là hội viên vắng.
public record AttendanceHistoryItem(string Type, int ReferenceId, string Title, DateOnly Date, TimeOnly StartTime,
    TimeOnly EndTime, string? Room, string Coach, AttendanceStatus? Status, int? ClassId = null);
public record AttendanceHistoryResponse(int Year, int Month, int CountedSessions, int AttendedSessions, decimal AttendanceRate,
    List<AttendanceHistoryItem> Items, string Formula, bool HasData = true);
public record CoachScheduleItem(string Type, int ReferenceId, string Title, string Sport, DateOnly Date, TimeOnly StartTime,
    TimeOnly EndTime, int? RoomId, string? Room, int ParticipantCount, SessionStatus Status,
    AttendanceStatus? CoachAttendanceStatus = null, DateTime? CoachCheckedInAt = null);
public record RosterItem(int MemberId, string FullName, string Email, int BookingId, AttendanceStatus Status,
    bool AttendanceRecorded = false, bool AttendanceFinalized = false, string CheckInState = "NotCheckedIn",
    DateTime? CheckedInAt = null, AttendanceSource? Source = null, RosterMemberProfile? Profile = null);
public record AttendanceEntry(int MemberId, AttendanceStatus Status = AttendanceStatus.Absent);
public record BulkAttendanceRequest([param: Required, MinLength(1)] List<AttendanceEntry> Items);
public record ClassCheckInRequest([param: Required, StringLength(64, MinimumLength = 6)] string Code);
public record ClassCheckInCodeDto(int SessionId, string Code, DateTime ExpiresAt, string QrPayload);
public record ClassSelfCheckInResult(string ActorType, int SessionId, int UserId, AttendanceStatus Status,
    DateTime CheckedInAt, bool AlreadyCheckedIn);

public interface IScheduleAttendanceService
{
    Task<List<PersonalScheduleItem>> GetPersonalScheduleAsync(int memberId, DateOnly from, DateOnly to, bool includeCancelled, CancellationToken ct);
    Task<List<PersonalScheduleItem>> GetMemberScheduleForStaffAsync(int actorId, int memberId, DateOnly from,
        DateOnly to, bool includeCancelled, CancellationToken ct);
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
    IFlow2TimeRules timeRules, IOptions<SchedulingOptions> options, IConfiguration configuration) : IScheduleAttendanceService
{
    private readonly SchedulingOptions settings = options.Value;
    // Hội viên xem lịch lớp và tập riêng của mình.
    // Nghiệp vụ: Kiểm tra hội viên đang hoạt động và khoảng ngày; chỉ ghép cả lịch hủy khi được yêu cầu.
    public async Task<List<PersonalScheduleItem>> GetPersonalScheduleAsync(int memberId, DateOnly from, DateOnly to, bool includeCancelled, CancellationToken ct)
    {
        ValidateRange(from, to, 180);
        await EnsureMemberAsync(memberId, ct);
        return await ReadMemberScheduleAsync(memberId, from, to, includeCancelled, ct);
    }

    // Nhân viên xem lịch hội viên đã chọn.
    // Nghiệp vụ: Chỉ lễ tân hoặc quản lý có tài khoản hoạt động; đối tượng phải là hội viên, đọc cùng cách lịch cá nhân.
    public async Task<List<PersonalScheduleItem>> GetMemberScheduleForStaffAsync(int actorId, int memberId, DateOnly from,
        DateOnly to, bool includeCancelled, CancellationToken ct)
    {
        if (from == default || to == default)
            throw new SchedulingException("INVALID_DATE_RANGE", "Phải truyền from và to hợp lệ.");
        ValidateRange(from, to, 180);
        var actor = await users.GetUserAsync(actorId, ct);
        if (actor is not { IsActive: true } || actor.Role is not (UserRole.Manager or UserRole.Receptionist))
            throw SchedulingException.Forbidden("ROLE_NOT_ALLOWED", "Vai trò không được xem lịch hội viên.");
        var member = await users.GetUserAsync(memberId, ct);
        if (member?.Role != UserRole.Member)
            throw SchedulingException.NotFound("MEMBER_NOT_FOUND", "Không tìm thấy hội viên.");
        return await ReadMemberScheduleAsync(memberId, from, to, includeCancelled, ct);
    }

    private async Task<List<PersonalScheduleItem>> ReadMemberScheduleAsync(int memberId, DateOnly from, DateOnly to,
        bool includeCancelled, CancellationToken ct)
    {
        var classBookings = await attendances.GetMemberClassBookingsAsync(memberId, from, to, includeCancelled, ct);
        var ptBookings = await attendances.GetMemberPtBookingsAsync(memberId, from, to, includeCancelled, ct);
        var classAttendance = await attendances.GetClassAttendanceMapAsync(memberId, classBookings.Select(x => x.SessionId).ToList(), ct);
        var ptAttendance = await attendances.GetPtAttendanceMapAsync(memberId, ptBookings.Select(x => x.Id).ToList(), ct);
        var classItems = classBookings.Select(x => new PersonalScheduleItem("Class", x.Id, x.SessionId, x.Session.ClassName,
            x.Session.Class.Sport.Name, x.Session.Date, x.Session.StartTime, x.Session.EndTime, x.Session.RoomId, x.Session.Room.Name,
            x.Session.CoachId!.Value, x.Session.Coach!.FullName, x.Status,
            classAttendance.TryGetValue(x.SessionId, out var cs) ? cs : null, x.EnrollmentId));
        var ptItems = ptBookings.Select(x => new PersonalScheduleItem("PT", x.Id, x.Id, "Personal Training", x.Sport.Name,
            x.Date, x.StartTime, x.EndTime, x.RoomId, x.Room == null ? null : x.Room.Name, x.CoachId, x.Coach.FullName, x.Status,
            ptAttendance.TryGetValue(x.Id, out var ps) ? ps : null, null, x.FundingSource));
        return classItems.Concat(ptItems).OrderBy(x => x.Date).ThenBy(x => x.StartTime).ToList();
    }

    // Tổng hợp chuyên cần theo tháng.
    // Nghiệp vụ: Chỉ tính buổi hợp lệ đã kết thúc; có mặt hoặc đến muộn là tham dự, không có dữ liệu thì tỷ lệ bằng không.
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
            return new(type, x.ReferenceId, x.Title, x.Date, x.StartTime, x.EndTime, x.Room, x.Coach,
                ended ? x.Status ?? AttendanceStatus.Absent : null, x.ClassId);
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

    // Huấn luyện viên xem các buổi được phân công.
    // Nghiệp vụ: Kiểm tra tài khoản huấn luyện viên đang hoạt động và khoảng ngày; trả lịch lớp, tập riêng, số học viên và điểm danh người dạy.
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

    // Huấn luyện viên đọc danh sách học viên của lớp mình.
    // Nghiệp vụ: Kiểm tra đúng buổi phụ trách; trả kèm trạng thái đã ghi nhận điểm danh và tình trạng tự điểm danh.
    public async Task<List<RosterItem>> GetClassRosterAsync(int coachId, int sessionId, CancellationToken ct)
    {
        await OwnsClassSessionAsync(coachId, sessionId, ct);
        return await ClassRosterAsync(sessionId, ct);
    }

    // Huấn luyện viên đọc học viên của buổi tập riêng.
    // Nghiệp vụ: Kiểm tra lịch đặt do đúng huấn luyện viên phụ trách trước khi trả hồ sơ và trạng thái tham dự.
    public async Task<List<RosterItem>> GetPtRosterAsync(int coachId, int bookingId, CancellationToken ct)
    {
        await OwnsPtBookingAsync(coachId, bookingId, ct);
        return await PtRosterAsync(bookingId, ct);
    }

    // Cấp hoặc dùng lại mã điểm danh lớp.
    // Nghiệp vụ: Khóa buổi; hội viên cần lịch đặt hợp lệ, huấn luyện viên cần đúng buổi; áp khung giờ theo cấu hình.
    // Tái dựng mã còn hạn từ thông tin đã lưu và khóa bí mật ổn định; hết hạn mới cấp lại, chưa ghi điểm danh.
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
            if (actor.Role is not (UserRole.Member or UserRole.Coach or UserRole.Receptionist or UserRole.Manager))
                throw SchedulingException.Forbidden("CHECK_IN_CODE_ISSUER_REQUIRED", "Vai trò không được mở mã check-in lớp.");
            if (actor.Role == UserRole.Member)
                await RequireEligibleClassBookingAsync(actorId, session, token);
            ValidateCheckInWindow(session);

            // Tái dựng mã còn hiệu lực từ thông tin đã lưu khi đang giữ khóa buổi.
            // Mở lại từ màn hình có quyền không được làm mất hiệu lực mã đã chia sẻ.
            if (session.CheckInCodeIssuedAt is { } previousIssuedAt &&
                session.CheckInCodeIssuedByUserId is { } previousIssuer &&
                session.CheckInCodeExpiresAt is { } previousExpiry &&
                (!timeRules.EnforceTimeRules || previousExpiry > clock.UtcNow))
            {
                var previousCode = GenerateCheckInCode(sessionId, previousIssuedAt, previousIssuer);
                if (CheckInCodeMatches(session, previousCode))
                    return CheckInCodeDto(sessionId, previousCode, previousExpiry);
            }
            // Cơ sở dữ liệu giữ độ chính xác phần triệu giây; thời gian trong ứng dụng còn có đơn vị nhỏ hơn.
            var issuedAt = new DateTime(clock.UtcNow.Ticks / 10 * 10, DateTimeKind.Utc);
            var code = GenerateCheckInCode(sessionId, issuedAt, actorId);
            var endUtc = clock.ToUtc(session.Date, session.EndTime);
            var expiresAt = new[] { issuedAt.AddMinutes(settings.ClassCheckInCodeLifetimeMinutes), endUtc }.Min();
            session.CheckInCodeHash = HashCode(code);
            session.CheckInCodeIssuedAt = issuedAt;
            session.CheckInCodeExpiresAt = expiresAt;
            session.CheckInCodeIssuedByUserId = actorId;
            audit.AddAudit(actorId, "ClassSession.CheckInCodeIssued", nameof(ClassSession), sessionId,
                new { ExpiresAt = expiresAt });
            return CheckInCodeDto(sessionId, code, expiresAt);
        }, ct);

    // Ghi nhận hội viên tự điểm danh lớp bằng mã.
    // Nghiệp vụ: Kiểm tra khung giờ, mã, lịch đặt và quyền gói; đã ghi thì trả bản cũ, không nhân bản hay trừ tiền.
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
            var booking = await RequireEligibleClassBookingAsync(memberId, session, token);
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

    // Huấn luyện viên tự điểm danh buổi lớp được phân công.
    // Nghiệp vụ: Kiểm tra quyền phụ trách, khung giờ và mã; lưu điểm danh người dạy riêng, không đổi điểm danh hội viên.
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

    // Chặn huấn luyện viên điểm danh lớp bằng tay.
    // Nghiệp vụ: Luôn báo lớp chỉ cho tự điểm danh; hội viên dùng chức năng tự điểm danh.
    public Task<List<RosterItem>> MarkClassAttendanceAsync(
        int coachId, int sessionId, BulkAttendanceRequest request, CancellationToken ct) =>
        Task.FromException<List<RosterItem>>(SchedulingException.Forbidden(
            "CLASS_ATTENDANCE_SELF_CHECKIN_ONLY",
            "Hội viên phải tự check-in; huấn luyện viên chỉ được xem roster."));

    // Huấn luyện viên ghi tham dự buổi tập riêng.
    // Nghiệp vụ: Đúng huấn luyện viên, hội viên và lịch đã xác nhận; đúng ngày, đã đến giờ khi bật ràng buộc rồi mới chốt.
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
            x.Status is not null, ended, state, x.CheckedInAt, x.Source, x.Profile);
    }

    private static RosterItem ToPtRosterItem(RosterRow x)
    {
        var state = x.Status?.ToString() ?? (x.LegacyFinalized ? AttendanceStatus.Absent.ToString() : "NotCheckedIn");
        return new(x.MemberId, x.FullName, x.Email, x.BookingId, x.Status ?? AttendanceStatus.Absent,
            x.Status is not null, x.LegacyFinalized, state, x.CheckedInAt, x.Source, x.Profile);
    }

    // Kiểm tra quyền điểm danh lớp.
    // Nghiệp vụ: Cần lịch đặt đã xác nhận; nếu dùng gói thì gói phải đủ quyền tại ngày học.
    private async Task<ClassCheckInBookingRow> RequireEligibleClassBookingAsync(int memberId, ClassSession session, CancellationToken ct)
    {
        var booking = await attendances.FindConfirmedClassBookingAsync(session.Id, memberId, ct)
            ?? throw SchedulingException.Forbidden("CLASS_BOOKING_REQUIRED", "Bạn không có booking hiệu lực cho buổi này.");
        if (booking.SubscriptionId != null) _ = (await subscriptions.ListForMemberAndSportAsync(memberId, booking.SportId, ct))
            .FirstOrDefault(x => x.Id == booking.SubscriptionId &&
                SchedulingRules.SubscriptionCovers(x, booking.SportId, session.Date,
                    needsClass: true, enforceTimeRules: timeRules.EnforceTimeRules))
            ?? throw SchedulingException.Conflict("SUBSCRIPTION_NOT_ELIGIBLE", "Gói dùng đặt lớp không còn hiệu lực cho buổi học.");
        return booking;
    }

    // Kiểm tra thời điểm được nhận điểm danh lớp.
    // Nghiệp vụ: Buổi phải ở trạng thái đã lên lịch; bình thường mở trước giờ bắt đầu theo cấu hình và đóng đúng giờ kết thúc.
    private void ValidateCheckInWindow(ClassSession session)
    {
        if (session.Status == SessionStatus.Cancelled)
            throw SchedulingException.Conflict("SESSION_CANCELLED", "Không thể check-in buổi đã hủy.");
        if (session.Status != SessionStatus.Scheduled)
            throw SchedulingException.Conflict("SESSION_NOT_CHECK_IN_ELIGIBLE", "Buổi học không còn mở check-in.");
        if (!timeRules.EnforceTimeRules) return;
        var opensAt = clock.ToUtc(session.Date, session.StartTime).AddMinutes(-settings.ClassCheckInOpensMinutesBefore);
        var closesAt = clock.ToUtc(session.Date, session.EndTime);
        if (clock.UtcNow < opensAt)
            throw SchedulingException.Conflict("CLASS_CHECK_IN_NOT_OPEN", $"Check-in mở trước giờ bắt đầu {settings.ClassCheckInOpensMinutesBefore} phút.");
        if (clock.UtcNow >= closesAt)
            throw SchedulingException.Conflict("CLASS_CHECK_IN_CLOSED", "Thời gian check-in của buổi học đã kết thúc.");
    }

    // Kiểm tra mã đúng buổi và hiệu lực.
    // Nghiệp vụ: Mã phải khớp mã băm; bật ràng buộc thì chặn hết hạn, bỏ qua thời gian vẫn phải kiểm tra nội dung mã.
    private void ValidateCheckInCode(ClassSession session, string rawCode)
    {
        if (session.CheckInCodeHash is null || session.CheckInCodeExpiresAt is null)
            throw SchedulingException.Conflict("CLASS_CHECK_IN_CODE_REQUIRED", "Buổi học chưa có mã check-in đang hoạt động.");
        if (timeRules.EnforceTimeRules && session.CheckInCodeExpiresAt <= clock.UtcNow)
            throw SchedulingException.Conflict("CLASS_CHECK_IN_CODE_EXPIRED", "Mã check-in đã hết hạn.");
        if (!CheckInCodeMatches(session, rawCode))
            throw new SchedulingException("CLASS_CHECK_IN_CODE_INVALID", "Mã không khớp buổi học này hoặc đã được thay mới. Vui lòng lấy mã hiện tại của đúng buổi từ HLV/lễ tân.");
    }

    // Phân loại có mặt hoặc đến muộn.
    // Nghiệp vụ: Trong thời gian ân hạn, kể cả đúng ranh giới, là có mặt; sau đó là đến muộn.
    private AttendanceStatus ResolveCheckInStatus(ClassSession session) =>
        clock.UtcNow <= clock.ToUtc(session.Date, session.StartTime).AddMinutes(settings.ClassPresentGraceMinutes)
            ? AttendanceStatus.Present
            : AttendanceStatus.Late;

    private static ClassSelfCheckInResult ToCheckInResult(string actorType, int sessionId, int userId,
        AttendanceStatus status, DateTime checkedInAt, bool alreadyCheckedIn) =>
        new(actorType, sessionId, userId, status, checkedInAt, alreadyCheckedIn);

    // Tái dựng mã ngắn bằng mã xác thực có khóa bí mật.
    // Nghiệp vụ: Dùng khóa bí mật cùng mã buổi, giờ cấp và người cấp; không lưu mã điểm danh rõ trong cơ sở dữ liệu.
    private string GenerateCheckInCode(int sessionId, DateTime issuedAt, int issuerId)
    {
        // Tách miền xác thực để chỉ máy chủ giữ khóa bí mật mới tái dựng được mã ngắn.
        // Giữ cách lưu mã băm hiện tại; không lưu mã rõ, thêm cột hay thay cấu trúc cơ sở dữ liệu.
        var secret = configuration["Scheduling:ClassCheckInCodeKey"] ?? configuration["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(secret))
            throw new SchedulingException("CHECK_IN_CONFIGURATION_MISSING", "Chưa cấu hình mã check-in trên Backend. Vui lòng liên hệ quản lý.", 503);
        var payload = FormattableString.Invariant($"sportscenter:class-check-in:v1:{sessionId}:{issuedAt.Ticks}:{issuerId}");
        var bytes = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        return string.Concat(bytes.Take(8).Select(value => alphabet[value & 31]));
    }

    private static bool CheckInCodeMatches(ClassSession session, string code) =>
        session.CheckInCodeHash is { } hash && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(hash), Encoding.UTF8.GetBytes(HashCode(code)));

    private static ClassCheckInCodeDto CheckInCodeDto(int sessionId, string code, DateTime expiresAt) =>
        new(sessionId, code, expiresAt, JsonSerializer.Serialize(new { type = "ClassSessionCheckIn", sessionId, code }));

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

    // Kiểm tra ngày và giờ chốt buổi tập riêng.
    // Nghiệp vụ: Chặn buổi hủy; bật ràng buộc thì phải đúng ngày Việt Nam và đã tới giờ bắt đầu.
    private void ValidateAttendanceDay(DateOnly date, TimeOnly startTime, bool cancelled)
    {
        if (cancelled) throw SchedulingException.Conflict("SESSION_CANCELLED", "Không thể điểm danh buổi đã hủy.");
        if (!timeRules.EnforceTimeRules) return;
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
