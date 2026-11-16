using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;

namespace SportsCenter.Services.Scheduling;

public record CoachTeachingSummary(int CoachId, string FullName, string? AvatarUrl, bool IsActive,
    int PtTaught, int ClassSessionsTaught, int CoursesTaught, int NotEnded, int Unverified, int NotTaught, int Cancelled);
public record CoachTeachingItem(int ReferenceId, string Type, int? CourseId, string Title, string Sport,
    DateOnly Date, TimeOnly StartTime, TimeOnly EndTime, string? Room, string Result);
public record CoachTeachingReport(int Year, int Quarter, DateOnly From, DateOnly To, DateTime AsOfUtc,
    CoachTeachingSummary Coach, List<CoachTeachingItem> Items, string CountingRule);
public record CoachTeachingOverview(int Year, int Quarter, DateOnly From, DateOnly To, DateTime AsOfUtc,
    List<CoachTeachingSummary> Coaches, string CountingRule);

public sealed class CoachTeachingService(ICoachTeachingRepository reports, IScheduleUserRepository users, IAppClock clock)
{
    public const string CountingRule = "Chỉ tính buổi đã kết thúc, có điểm danh Present/Late hoặc trạng thái hoàn thành; không tính buổi hủy/vắng. Khóa đã dạy là khóa nhiều tuần có ít nhất một buổi được tính trong quý; mỗi khóa chỉ tính một lần cho mỗi HLV trong quý.";

    // Huấn luyện viên xem giảng dạy của mình theo quý.
    // Nghiệp vụ: Kiểm tra tài khoản huấn luyện viên đang hoạt động; đọc đúng quý và năm, không tính lịch tương lai là đã dạy.
    public async Task<CoachTeachingReport> GetOwnAsync(int actorId, int year, int quarter, CancellationToken ct)
    {
        await users.EnsureActiveActorAsync(actorId, UserRole.Coach, ct);
        return await ReadCoachAsync(actorId, year, quarter, ct);
    }

    // Quản lý xem giảng dạy của một huấn luyện viên trong quý.
    // Nghiệp vụ: Kiểm tra tài khoản quản lý đang hoạt động rồi tổng hợp cùng quy tắc báo cáo cá nhân.
    public async Task<CoachTeachingReport> GetCoachAsync(int actorId, int coachId, int year, int quarter, CancellationToken ct)
    {
        await users.EnsureActiveActorAsync(actorId, UserRole.Manager, ct);
        return await ReadCoachAsync(coachId, year, quarter, ct);
    }

    // Quản lý xem tổng hợp giảng dạy toàn trung tâm.
    // Nghiệp vụ: Nhóm theo huấn luyện viên trong quý; tách đã dạy, chưa kết thúc, chưa xác nhận, vắng và hủy.
    public async Task<CoachTeachingOverview> GetOverviewAsync(int actorId, int year, int quarter, CancellationToken ct)
    {
        await users.EnsureActiveActorAsync(actorId, UserRole.Manager, ct);
        var (from, to) = QuarterRange(year, quarter);
        var data = await reports.ReadAsync(from, to.AddDays(1), null, ct);
        var asOf = clock.UtcNow;
        var sessions = data.Sessions.GroupBy(row => row.CoachId).ToDictionary(group => group.Key, group => group.ToList());
        return new(year, quarter, from, to, asOf, data.Coaches.Select(coach => Summarize(coach,
            ToItems(sessions.GetValueOrDefault(coach.Id) ?? [], asOf))).ToList(), CountingRule);
    }

    private async Task<CoachTeachingReport> ReadCoachAsync(int coachId, int year, int quarter, CancellationToken ct)
    {
        var (from, to) = QuarterRange(year, quarter);
        var data = await reports.ReadAsync(from, to.AddDays(1), coachId, ct);
        var coach = data.Coaches.SingleOrDefault(x => x.Id == coachId)
            ?? throw SchedulingException.NotFound("COACH_NOT_FOUND", "Không tìm thấy huấn luyện viên.");
        var asOf = clock.UtcNow;
        var items = ToItems(data.Sessions.Where(row => row.CoachId == coachId).ToList(), asOf);
        return new(year, quarter, from, to, asOf, Summarize(coach, items), items, CountingRule);
    }

    // Xác định khoảng ngày của quý báo cáo.
    // Nghiệp vụ: Chấp nhận quý 1-4 và năm hợp lệ; trả từ đầu quý đến ngày cuối quý theo lịch.
    public static (DateOnly From, DateOnly To) QuarterRange(int year, int quarter)
    {
        if (year is < 2000 or > 2200 || quarter is < 1 or > 4)
            throw new SchedulingException("INVALID_QUARTER", "Chọn quý từ 1 đến 4 và năm từ 2000 đến 2200.");
        var from = new DateOnly(year, (quarter - 1) * 3 + 1, 1);
        return (from, from.AddMonths(3).AddDays(-1));
    }

    private List<CoachTeachingItem> ToItems(List<CoachTeachingRow> rows, DateTime asOf) => rows
        .DistinctBy(row => (row.Type, row.ReferenceId))
        .Select(row => new CoachTeachingItem(row.ReferenceId, row.Type, row.CourseId, row.Title, row.Sport,
            row.Date, row.StartTime, row.EndTime, row.Room, Result(row, asOf)))
        .OrderByDescending(row => row.Date).ThenBy(row => row.StartTime).ThenBy(row => row.ReferenceId).ToList();

    private string Result(CoachTeachingRow row, DateTime asOf)
    {
        if (row.Cancelled) return "Cancelled";
        if (clock.ToUtc(row.Date, row.EndTime) > asOf) return "NotEnded";
        if (row.AttendanceStatus is AttendanceStatus.Absent or AttendanceStatus.Excused) return "NotTaught";
        if (row.AttendanceStatus is AttendanceStatus.Present or AttendanceStatus.Late || row.Completed) return "Taught";
        return "Unverified";
    }

    private static CoachTeachingSummary Summarize(TeachingCoachRow coach, List<CoachTeachingItem> items)
    {
        var taught = items.Where(row => row.Result == "Taught").ToList();
        return new(coach.Id, coach.FullName, coach.AvatarUrl, coach.IsActive,
            taught.Count(row => row.Type == "PT"), taught.Count(row => row.Type == "Class"),
            taught.Where(row => row.CourseId != null).Select(row => row.CourseId).Distinct().Count(),
            items.Count(row => row.Result == "NotEnded"), items.Count(row => row.Result == "Unverified"),
            items.Count(row => row.Result == "NotTaught"), items.Count(row => row.Result == "Cancelled"));
    }
}
