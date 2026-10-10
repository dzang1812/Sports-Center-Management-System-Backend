using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

public record ResourceConflict(DateOnly Date, TimeOnly StartTime, TimeOnly EndTime, string Resource, int ResourceId);

// Overlap checks across class sessions, class bookings and PT bookings. Shared by class scheduling, class booking and PT.
public interface IScheduleConflictRepository
{
    Task<bool> HasMemberConflictAsync(int memberId, DateOnly date, TimeOnly start, TimeOnly end, int? ignoredPtId, CancellationToken ct);
    Task<bool> HasCoachConflictAsync(int coachId, DateOnly date, TimeOnly start, TimeOnly end, int? ignoredPtId, CancellationToken ct);
    Task<bool> IsRoomBusyAsync(int roomId, DateOnly date, TimeOnly start, TimeOnly end, int? ignoredPtId, CancellationToken ct);
    Task<List<ResourceConflict>> FindResourceConflictsAsync(List<DateOnly> dates, TimeOnly start, TimeOnly end,
        int roomId, int coachId, int? ignoredSessionId, CancellationToken ct);
    Task<List<ResourceConflict>> FindMemberConflictsAsync(List<int> memberIds, DateOnly date, TimeOnly start, TimeOnly end,
        int? ignoredSessionId, int? ignoredPtId, CancellationToken ct);
}

public class ScheduleConflictRepository(AppDbContext db) : IScheduleConflictRepository
{
    public async Task<bool> HasMemberConflictAsync(int memberId, DateOnly date, TimeOnly start, TimeOnly end, int? ignoredPtId, CancellationToken ct) =>
        await db.ClassBookings.AnyAsync(x => x.MemberId == memberId && x.Status == BookingStatus.Confirmed && x.Session.Status != SessionStatus.Cancelled &&
            x.Session.Date == date && x.Session.StartTime < end && start < x.Session.EndTime, ct) ||
        await db.PtBookings.AnyAsync(x => x.MemberId == memberId && x.Status == BookingStatus.Confirmed && x.Id != ignoredPtId &&
            x.Date == date && x.StartTime < end && start < x.EndTime, ct);

    public async Task<bool> HasCoachConflictAsync(int coachId, DateOnly date, TimeOnly start, TimeOnly end, int? ignoredPtId, CancellationToken ct) =>
        await db.ClassSessions.AnyAsync(x => x.CoachId == coachId && x.Status != SessionStatus.Cancelled && x.Date == date &&
            x.StartTime < end && start < x.EndTime, ct) ||
        await db.PtBookings.AnyAsync(x => x.CoachId == coachId && x.Status == BookingStatus.Confirmed && x.Id != ignoredPtId &&
            x.Date == date && x.StartTime < end && start < x.EndTime, ct);

    public async Task<bool> IsRoomBusyAsync(int roomId, DateOnly date, TimeOnly start, TimeOnly end, int? ignoredPtId, CancellationToken ct) =>
        await db.ClassSessions.AnyAsync(x => x.RoomId == roomId && x.Status != SessionStatus.Cancelled && x.Date == date && x.StartTime < end && start < x.EndTime, ct) ||
        await db.PtBookings.AnyAsync(x => x.RoomId == roomId && x.Status == BookingStatus.Confirmed && x.Id != ignoredPtId && x.Date == date && x.StartTime < end && start < x.EndTime, ct);

    public async Task<List<ResourceConflict>> FindResourceConflictsAsync(List<DateOnly> dates, TimeOnly start, TimeOnly end,
        int roomId, int coachId, int? ignoredSessionId, CancellationToken ct)
    {
        var result = await db.ClassSessions.Where(x => dates.Contains(x.Date) && x.Status != SessionStatus.Cancelled && x.Id != ignoredSessionId &&
                x.StartTime < end && start < x.EndTime && (x.RoomId == roomId || x.CoachId == coachId))
            .Select(x => new ResourceConflict(x.Date, x.StartTime, x.EndTime, x.RoomId == roomId ? "Room" : "Coach", x.RoomId == roomId ? roomId : coachId)).ToListAsync(ct);
        result.AddRange(await db.PtBookings.Where(x => dates.Contains(x.Date) && x.Status == BookingStatus.Confirmed && x.StartTime < end && start < x.EndTime &&
                (x.CoachId == coachId || (x.RoomId != null && x.RoomId == roomId)))
            .Select(x => new ResourceConflict(x.Date, x.StartTime, x.EndTime, x.CoachId == coachId ? "Coach" : "Room", x.CoachId == coachId ? coachId : roomId)).ToListAsync(ct));
        return result;
    }

    public async Task<List<ResourceConflict>> FindMemberConflictsAsync(List<int> memberIds, DateOnly date, TimeOnly start, TimeOnly end,
        int? ignoredSessionId, int? ignoredPtId, CancellationToken ct)
    {
        var conflicts = await db.ClassBookings.Where(x => memberIds.Contains(x.MemberId) && x.Status == BookingStatus.Confirmed && x.SessionId != ignoredSessionId &&
                x.Session.Status != SessionStatus.Cancelled && x.Session.Date == date && x.Session.StartTime < end && start < x.Session.EndTime)
            .Select(x => new ResourceConflict(date, x.Session.StartTime, x.Session.EndTime, "Member", x.MemberId)).ToListAsync(ct);
        conflicts.AddRange(await db.PtBookings.Where(x => memberIds.Contains(x.MemberId) && x.Status == BookingStatus.Confirmed && x.Id != ignoredPtId &&
                x.Date == date && x.StartTime < end && start < x.EndTime)
            .Select(x => new ResourceConflict(date, x.StartTime, x.EndTime, "Member", x.MemberId)).ToListAsync(ct));
        return conflicts;
    }
}
