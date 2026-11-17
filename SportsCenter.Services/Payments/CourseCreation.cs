using SportsCenter.Repository.Entities;
using System.Text.Json.Serialization;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.Services.Payments;

public record CreateCourseRequest(string Name, int SportId, int CoachId, int RoomId, int Capacity, Level Level,
    string? Description, DateOnly StartDate, int Weeks, DayOfWeek[] DaysOfWeek,
    [property: JsonRequired] TimeOnly StartTime, [property: JsonRequired] TimeOnly EndTime,
    decimal SessionPrice, DateTime EnrollmentClosesAt, bool OpenRegistration = false);
public record CoursePreviewSession(DateOnly Date, DayOfWeek DayOfWeek, TimeOnly StartTime, TimeOnly EndTime,
    int CoachId, string Coach, int RoomId, string Room);
public record CoursePreview(int Weeks, int SessionCount, decimal SessionPrice, decimal TotalFee,
    DateOnly RangeStart, DateOnly RangeEndExclusive, CoursePreviewSession FirstSession, CoursePreviewSession LastSession,
    DateTime EnrollmentClosesAt, bool CanOpen, List<CoursePreviewSession> Sessions, List<ResourceConflict> Conflicts);
public record CreatedCourse(int ClassId, ClassStatus Status, int SessionCount, decimal TotalFee);
public record CourseSetupIssue(string Field, string ErrorCode, string Message);

public sealed partial class Flow2CommerceService
{
    // Xem trước lịch và học phí nguyên khóa.
    // Nghiệp vụ: Kiểm tra quyền quản lý, tài nguyên, khoảng lịch và lịch trùng; chỉ tính trước, chưa ghi dữ liệu hay giữ chỗ.
    public async Task<CoursePreview> PreviewCourseAsync(int actor, CreateCourseRequest request, CancellationToken ct)
    {
        await users.EnsureActiveActorAsync(actor, UserRole.Manager, ct);
        return await BuildCoursePlanAsync(request, ct);
    }

    // Tạo nguyên khóa cùng toàn bộ buổi học.
    // Nghiệp vụ: Khóa bộ môn, huấn luyện viên và phòng rồi kiểm tra cả đợt; tạo đủ lịch và mở bán theo yêu cầu trong cùng giao dịch dữ liệu.
    public Task<CreatedCourse> CreateCourseAsync(int actor, CreateCourseRequest request, string key, CancellationToken ct)
    {
        // Vẫn kiểm tra quyền khi nhận lại yêu cầu cũ; kiểm tra khóa chống trùng và nội dung yêu cầu trước khi trả kết quả.
        return CreateAuthorizedAsync();
        async Task<CreatedCourse> CreateAuthorizedAsync()
        {
            await users.EnsureActiveActorAsync(actor, UserRole.Manager, ct);
            return await wallet.ExecuteAsync(actor, "course-create", key, request, async token =>
            {
                await tx.LockAsync([new(ScheduleResourceType.Sport, request.SportId)], token);
                await tx.LockAsync([new(ScheduleResourceType.Coach, request.CoachId), new(ScheduleResourceType.Room, request.RoomId)], token);
                var plan = await BuildCoursePlanAsync(request, token);
                if (plan.Conflicts.Count != 0)
                    throw new SchedulingException("COURSE_SCHEDULE_CONFLICT", "Lịch trùng phòng hoặc HLV; chưa tạo khóa hoặc buổi nào.", 409,
                        new { field = "schedule", conflicts = plan.Conflicts });
                var course = new Class
                {
                    Name = request.Name.Trim(), Description = request.Description?.Trim(), SportId = request.SportId,
                    CoachId = request.CoachId, RoomId = request.RoomId, Capacity = request.Capacity, Level = request.Level,
                    StartDate = plan.RangeStart, EndDate = plan.RangeEndExclusive.AddDays(-1),
                    BookingMode = ClassBookingMode.CourseEnrollment, Status = ClassStatus.Draft,
                    CourseFee = plan.TotalFee, ExpectedSessions = plan.SessionCount, EnrollmentClosesAt = request.EnrollmentClosesAt,
                    CourseStartsAt = clock.ToUtc(plan.FirstSession.Date, plan.FirstSession.StartTime),
                };
                repo.Add(course);
                await tx.SaveChangesAsync(token);
                foreach (var s in plan.Sessions)
                    repo.Add(new ClassSession
                    {
                        ClassId = course.Id, ClassName = course.Name, Level = course.Level, Capacity = course.Capacity,
                        Date = s.Date, StartTime = s.StartTime, EndTime = s.EndTime, CoachId = s.CoachId, RoomId = s.RoomId,
                        CreatedAt = clock.UtcNow,
                    });
                await tx.SaveChangesAsync(token);
                if (request.OpenRegistration)
                {
                    await ValidateCourseAsync(course, token);
                    RequireOpeningWindow(plan);
                    course.Status = ClassStatus.Open;
                    course.PublishedAt = clock.UtcNow;
                }
                audit.AddAudit(actor, "Course.Create", nameof(Class), course.Id,
                    new { request, plan.SessionCount, plan.TotalFee, plan.RangeEndExclusive });
                await tx.SaveChangesAsync(token);
                RequireMinimumEnrollmentWindow(request.EnrollmentClosesAt);
                if (request.OpenRegistration) RequireOpeningWindow(plan);
                return new CreatedCourse(course.Id, course.Status, plan.SessionCount, plan.TotalFee);
            }, ct);
        }
    }

    // Tính các buổi từ số tuần và thứ đã chọn.
    // Nghiệp vụ: Sinh buổi trong khoảng từ ngày bắt đầu đến trước ngày bắt đầu cộng số tuần nhân bảy; kiểm tra học phí, hạn đăng ký, môn, huấn luyện viên và phòng.
    private async Task<CoursePreview> BuildCoursePlanAsync(CreateCourseRequest r, CancellationToken ct)
    {
        static void Invalid(string code, string message, string field) => throw new SchedulingException(code, message, 400, new { field });
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length is < 2 or > 150)
            Invalid("COURSE_NAME_INVALID", "Tên khóa cần 2–150 ký tự.", "name");
        if (r.Description?.Length > 1000) Invalid("COURSE_DESCRIPTION_INVALID", "Mô tả tối đa 1.000 ký tự.", "description");
        if (!Enum.IsDefined(r.Level)) Invalid("COURSE_LEVEL_INVALID", "Trình độ không hợp lệ.", "level");
        if (r.Weeks is < 1 or > 52) Invalid("COURSE_WEEKS_INVALID", "Số tuần phải từ 1 đến 52.", "weeks");
        if (r.StartDate == default) Invalid("COURSE_DATE_INVALID", "Chọn ngày bắt đầu khoảng lịch hợp lệ.", "startDate");
        if (r.DaysOfWeek is not { Length: > 0 } || r.DaysOfWeek.Any(d => !Enum.IsDefined(d)) || r.DaysOfWeek.Distinct().Count() != r.DaysOfWeek.Length)
            Invalid("COURSE_WEEKDAYS_INVALID", "Chọn ít nhất một thứ, không trùng thứ.", "daysOfWeek");
        if (r.StartTime >= r.EndTime) Invalid("COURSE_TIME_INVALID", "Giờ kết thúc phải sau giờ bắt đầu; không hỗ trợ qua đêm.", "endTime");
        if (r.SessionPrice <= 0 || r.SessionPrice != decimal.Truncate(r.SessionPrice) || r.SessionPrice > 1_000_000_000m)
            Invalid("COURSE_PRICE_INVALID", "Giá buổi phải là VND nguyên dương, tối đa 1 tỷ.", "sessionPrice");
        var count = r.Weeks * r.DaysOfWeek!.Length;
        var total = r.SessionPrice * count;
        if (total > 1_000_000_000m) Invalid("COURSE_TOTAL_TOO_LARGE", "Tổng học phí không được vượt 1 tỷ VND.", "sessionPrice");
        DateOnly end;
        try { end = r.StartDate.AddDays(7 * r.Weeks); }
        catch (ArgumentOutOfRangeException) { throw new SchedulingException("COURSE_DATE_INVALID", "Khoảng lịch vượt giới hạn ngày.", 400, new { field = "startDate" }); }
        var dates = Enumerable.Range(0, r.Weeks * 7).Select(r.StartDate.AddDays).Where(d => r.DaysOfWeek.Contains(d.DayOfWeek)).ToList();
        if (r.EnrollmentClosesAt == default)
            Invalid("COURSE_CUTOFF_REQUIRED", "Chưa có hạn chót đăng ký.", "enrollmentClosesAt");
        if (r.EnrollmentClosesAt.Kind != DateTimeKind.Utc)
            Invalid("COURSE_CUTOFF_INVALID", "Hạn đăng ký cần UTC có Z; giao diện nhập theo giờ Việt Nam.", "enrollmentClosesAt");
        RequireMinimumEnrollmentWindow(r.EnrollmentClosesAt);
        if (r.EnrollmentClosesAt >= clock.ToUtc(r.StartDate, TimeOnly.MinValue))
            Invalid("COURSE_START_BEFORE_CUTOFF", "Ngày bắt đầu phải sau ngày hạn chót đăng ký (giờ Việt Nam).", "startDate");
        var sport = await repo.GetSportOrDefaultAsync(r.SportId, ct);
        if (sport is null || sport.Status != SportStatus.Active || !sport.HasClasses)
            Invalid("COURSE_SPORT_UNAVAILABLE", "Chọn bộ môn lớp học đang hoạt động.", "sportId");
        var coach = await users.GetUserAsync(r.CoachId, ct);
        if (coach is not { IsActive: true, Role: UserRole.Coach } || !await users.IsCoachForSportAsync(r.CoachId, r.SportId, ct))
            Invalid("COURSE_COACH_UNAVAILABLE", "HLV phải đang hoạt động và đúng chuyên môn.", "coachId");
        var room = await repo.FindRoomAsync(r.RoomId, ct);
        if (room is null || room.Status != RoomStatus.Active) Invalid("COURSE_ROOM_UNAVAILABLE", "Chọn phòng đang hoạt động.", "roomId");
        if (r.Capacity < 1 || r.Capacity > 10000 || r.Capacity > room!.Capacity)
            Invalid("COURSE_CAPACITY_INVALID", "Sĩ số phải dương và không vượt sức chứa phòng.", "capacity");
        var sessions = dates.Select(d => new CoursePreviewSession(d, d.DayOfWeek, r.StartTime, r.EndTime, r.CoachId, coach!.FullName, r.RoomId, room!.Name)).ToList();
        var busy = await conflicts.FindResourceConflictsAsync(dates, r.StartTime, r.EndTime, r.RoomId, r.CoachId, null, ct);
        var plan = new CoursePreview(r.Weeks, count, r.SessionPrice, total, r.StartDate, end, sessions[0], sessions[^1],
            r.EnrollmentClosesAt, clock.UtcNow < r.EnrollmentClosesAt && clock.UtcNow < clock.ToUtc(dates[0], r.StartTime), sessions, busy);
        if (r.OpenRegistration) RequireOpeningWindow(plan);
        return plan;
    }

    private void RequireMinimumEnrollmentWindow(DateTime cutoff)
    {
        if (cutoff < clock.UtcNow.AddDays(7))
            throw new SchedulingException("COURSE_CUTOFF_INVALID", "Hạn chót đăng ký phải cách thời điểm tạo khóa ít nhất 7 ngày.", 400, new { field = "enrollmentClosesAt" });
    }

    // Kiểm tra thời điểm được mở đăng ký khóa.
    // Nghiệp vụ: Phải còn trước hạn đăng ký và buổi đầu; các mốc này độc lập với công tắc bỏ qua thời gian.
    private void RequireOpeningWindow(CoursePreview plan)
    {
        if (clock.UtcNow >= plan.EnrollmentClosesAt)
            throw new SchedulingException("COURSE_ENROLLMENT_CLOSED", "Hạn đăng ký đã qua; chưa mở đăng ký khóa.", 409, new { field = "enrollmentClosesAt" });
        if (clock.UtcNow >= clock.ToUtc(plan.FirstSession.Date, plan.FirstSession.StartTime))
            throw new SchedulingException("COURSE_ALREADY_STARTED", "Buổi đầu đã bắt đầu; không mở đăng ký.", 409, new { field = "startDate" });
    }

    private static List<CourseSetupIssue> CourseSetupIssues(Class c, int actual)
    {
        var issues = new List<CourseSetupIssue>();
        if (c.CourseFee <= 0) issues.Add(new("fee", "COURSE_PRICE_REQUIRED", "Chưa có học phí hợp lệ."));
        if (actual == 0) issues.Add(new("schedule", "COURSE_SCHEDULE_EMPTY", "Chưa có lịch học; hãy tạo buổi học trước khi mở đăng ký."));
        if (c.ExpectedSessions <= 0) issues.Add(new("expectedSessions", "COURSE_SESSION_COUNT_REQUIRED", "Chưa có số buổi công bố."));
        else if (actual != c.ExpectedSessions) issues.Add(new("expectedSessions", "COURSE_SESSION_COUNT_MISMATCH", $"Lịch có {actual} buổi, khác {c.ExpectedSessions} buổi đã công bố."));
        if (c.EnrollmentClosesAt is null) issues.Add(new("enrollmentClosesAt", "COURSE_CUTOFF_REQUIRED", "Chưa có hạn đăng ký."));
        return issues;
    }
}
