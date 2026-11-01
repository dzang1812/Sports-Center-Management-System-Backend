using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

public record TeachingCoachRow(int Id, string FullName, string? AvatarUrl, bool IsActive);
public record CoachTeachingRow(int CoachId, int ReferenceId, string Type, int? CourseId, string Title,
    string Sport, DateOnly Date, TimeOnly StartTime, TimeOnly EndTime, string? Room,
    bool Cancelled, bool Completed, AttendanceStatus? AttendanceStatus);
public record CoachTeachingData(List<TeachingCoachRow> Coaches, List<CoachTeachingRow> Sessions);

public interface ICoachTeachingRepository
{
    Task<CoachTeachingData> ReadAsync(DateOnly from, DateOnly toExclusive, int? coachId, CancellationToken ct);
}

// Read existing schedule/attendance records only; reporting never changes a booking or attendance.
public sealed class CoachTeachingRepository(AppDbContext db) : ICoachTeachingRepository
{
    public async Task<CoachTeachingData> ReadAsync(DateOnly from, DateOnly toExclusive, int? coachId, CancellationToken ct)
    {
        var coaches = await db.Users.AsNoTracking()
            .Where(x => x.Role == UserRole.Coach && (coachId == null || x.Id == coachId))
            .OrderBy(x => x.FullName).ThenBy(x => x.Id)
            .Select(x => new TeachingCoachRow(x.Id, x.FullName, x.AvatarUrl, x.IsActive)).ToListAsync(ct);
        var classes = await db.ClassSessions.AsNoTracking()
            .Where(x => x.CoachId != null && (coachId == null || x.CoachId == coachId) && x.Date >= from && x.Date < toExclusive)
            .Select(x => new CoachTeachingRow(x.CoachId!.Value, x.Id, "Class",
                x.Class.BookingMode == ClassBookingMode.CourseEnrollment ? x.ClassId : null,
                x.ClassName, x.Class.Sport.Name, x.Date, x.StartTime, x.EndTime, x.Room.Name,
                x.Status == SessionStatus.Cancelled, x.Status == SessionStatus.Completed,
                db.CoachSessionAttendances.Where(a => a.SessionId == x.Id && a.CoachId == x.CoachId)
                    .Select(a => (AttendanceStatus?)a.Status).FirstOrDefault())).ToListAsync(ct);
        var pt = await db.PtBookings.AsNoTracking()
            .Where(x => (coachId == null || x.CoachId == coachId) && x.Date >= from && x.Date < toExclusive)
            .Select(x => new CoachTeachingRow(x.CoachId, x.Id, "PT", null, "Personal Training", x.Sport.Name,
                x.Date, x.StartTime, x.EndTime, x.Room == null ? null : x.Room.Name,
                x.Status == BookingStatus.Cancelled, x.Status == BookingStatus.Completed,
                db.Attendances.Where(a => a.PtBookingId == x.Id && a.MemberId == x.MemberId)
                    .Select(a => (AttendanceStatus?)a.Status).FirstOrDefault())).ToListAsync(ct);
        return new(coaches, classes.Concat(pt).ToList());
    }
}
