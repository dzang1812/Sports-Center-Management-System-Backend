using System.ComponentModel.DataAnnotations;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;

namespace SportsCenter.Services.Scheduling;

public record WeekAvailabilityRange(int SportId, DateOnly Date, TimeOnly StartTime, TimeOnly EndTime);
public record ReplaceWeekAvailabilityRequest(DateOnly WeekStart, [param: Required] List<WeekAvailabilityRange> Items);
public record DatedAvailabilityItem(int Id, int SportId, string Sport, DateOnly Date, TimeOnly StartTime, TimeOnly EndTime);
public record BookedPtWindow(int BookingId, int SportId, DateOnly Date, TimeOnly StartTime, TimeOnly EndTime);
public record WeekAvailabilitySport(int Id, string Name);
public record WeekAvailabilityResponse(DateOnly WeekStart, DateOnly WeekEnd, List<DatedAvailabilityItem> Items,
    List<BookedPtWindow> Bookings, List<WeekAvailabilitySport> Sports, List<AvailabilityItem> LegacyTemplates);

public sealed partial class PtService
{
    private static DateOnly WeekStart(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
    private static DateOnly WeekEnd(DateOnly start)
    {
        if (start == DateOnly.MinValue || start > DateOnly.MaxValue.AddDays(-6) || start.DayOfWeek != DayOfWeek.Monday)
            throw new SchedulingException("INVALID_AVAILABILITY_WEEK", "Tuần phải bắt đầu vào thứ Hai và có đủ 7 ngày.");
        return start.AddDays(6);
    }

    public async Task<WeekAvailabilityResponse> GetWeekAvailabilityAsync(int coachId, DateOnly weekStart, CancellationToken ct)
    {
        var end = WeekEnd(weekStart);
        await users.EnsureActiveActorAsync(coachId, UserRole.Coach, ct);
        var items = await pt.GetActiveAvailabilityAsync(coachId, weekStart, end, ct);
        var bookings = await pt.GetConfirmedBookingsInRangeAsync(coachId, weekStart, end, ct);
        var sports = (await catalog.ListSportsAsync(ct)).Where(s => s.Status == SportStatus.Active && s.HasPt).ToList();
        var eligible = await users.GetPtSportIdsAsync(coachId, sports.Select(s => s.Id).ToList(), ct);
        var legacy = await pt.GetLegacyAvailabilityAsync(coachId, ct);
        return new(weekStart, end,
            items.Select(x => new DatedAvailabilityItem(x.Id, x.SportId, x.Sport.Name, x.Date!.Value, x.StartTime, x.EndTime)).ToList(),
            bookings.Select(x => new BookedPtWindow(x.Id, x.SportId, x.Date, x.StartTime, x.EndTime)).ToList(),
            sports.Where(s => eligible.Contains(s.Id)).Select(s => new WeekAvailabilitySport(s.Id, s.Name)).ToList(),
            legacy.Where(x => eligible.Contains(x.SportId)).Select(x => new AvailabilityItem(x.Id, x.SportId, x.Sport.Name, x.DayOfWeek, x.StartTime, x.EndTime)).ToList());
    }

    public async Task<WeekAvailabilityResponse> ReplaceWeekAvailabilityAsync(int actorId, ReplaceWeekAvailabilityRequest request, CancellationToken ct)
    {
        var end = WeekEnd(request.WeekStart);
        ValidateWeekAvailability(request.Items, request.WeekStart, end);
        return await transaction.ExecuteAsync(async token =>
        {
            await users.EnsureActiveActorAsync(actorId, UserRole.Coach, token);
            await EnsureCoachAsync(actorId, null, token);
            var sportIds = request.Items.Select(x => x.SportId).Distinct().ToList();
            await transaction.LockAsync(sportIds.Select(id => new ScheduleResourceLock(ScheduleResourceType.Sport, id))
                .Append(new ScheduleResourceLock(ScheduleResourceType.Coach, actorId)), token);
            var eligible = await users.GetPtSportIdsAsync(actorId, sportIds, token);
            if (eligible.Count != sportIds.Count) throw SchedulingException.Conflict("COACH_NOT_ELIGIBLE", "Có bộ môn PT không thuộc chuyên môn đang hoạt động của HLV.");
            var bookings = await pt.GetConfirmedBookingsInRangeAsync(actorId, request.WeekStart, end, token);
            var protectedBookings = timeRules.EnforceTimeRules ? bookings.Where(b => clock.ToUtc(b.Date, b.EndTime) > clock.UtcNow) : bookings;
            var uncovered = protectedBookings.Where(b => !request.Items.Any(a => a.SportId == b.SportId && a.Date == b.Date && a.StartTime <= b.StartTime && a.EndTime >= b.EndTime)).ToList();
            if (uncovered.Count > 0) throw SchedulingException.Conflict("AVAILABILITY_HAS_BOOKINGS", "Không thể bỏ khung giờ đang có booking PT trong tuần này.",
                uncovered.Select(b => new BookedPtWindow(b.Id, b.SportId, b.Date, b.StartTime, b.EndTime)).ToList());
            var current = await pt.GetActiveAvailabilityForUpdateAsync(actorId, request.WeekStart, end, token);
            foreach (var item in current) item.IsActive = false;
            // Release old unique keys before inserting replacements, within the same coach lock and transaction.
            await transaction.SaveChangesAsync(token);
            pt.AddAvailabilities(request.Items.Select(x => new CoachAvailability
            {
                CoachId = actorId, SportId = x.SportId, Date = x.Date, DayOfWeek = x.Date.DayOfWeek,
                StartTime = x.StartTime, EndTime = x.EndTime, CreatedAt = clock.UtcNow,
            }));
            audit.AddAudit(actorId, "CoachAvailability.ReplaceWeek", nameof(CoachAvailability), actorId, request);
            await transaction.SaveChangesAsync(token);
            return await GetWeekAvailabilityAsync(actorId, request.WeekStart, token);
        }, ct);
    }

    private static void ValidateWeekAvailability(List<WeekAvailabilityRange>? items, DateOnly from, DateOnly to)
    {
        if (items is null) throw new SchedulingException("INVALID_AVAILABILITY", "Thiếu danh sách khung giờ.");
        if (items.Count > 50) throw new SchedulingException("AVAILABILITY_LIMIT", "Tối đa 50 khung giờ trong một tuần.");
        foreach (var item in items)
        {
            if (item.Date < from || item.Date > to) throw new SchedulingException("AVAILABILITY_OUTSIDE_WEEK", "Mọi khung giờ phải nằm trong tuần đang chọn.");
            if (item.StartTime >= item.EndTime || item.StartTime.Ticks % TimeSpan.TicksPerHour != 0 || item.EndTime.Ticks % TimeSpan.TicksPerHour != 0)
                throw new SchedulingException("INVALID_AVAILABILITY", "Giờ bắt đầu/kết thúc phải đúng giờ, mỗi buổi PT dài 60 phút và không qua ngày.");
        }
        for (var i = 0; i < items.Count; i++)
        for (var j = i + 1; j < items.Count; j++)
            if (items[i].Date == items[j].Date && items[i].StartTime < items[j].EndTime && items[j].StartTime < items[i].EndTime)
                throw new SchedulingException("AVAILABILITY_OVERLAP", "Các khung giờ trong cùng ngày không được trùng nhau, kể cả khác bộ môn.");
    }
}
