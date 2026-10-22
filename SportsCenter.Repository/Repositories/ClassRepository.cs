using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

public record ClassResources(int SportId, int RoomId, int? CoachId);
public record SessionResources(int ClassId, int SportId, int RoomId, int? CoachId);
public record SessionRow(ClassSession Session, Class Class, string Sport, string Room, string? Coach, int Booked);
public record BookingKey(int MemberId, int SessionId);

// Classes, their sessions and class bookings (CLS-*, BK-*).
public interface IClassRepository
{
    // classes
    Task<(List<Class> Items, int Total)> ListClassesAsync(int page, int pageSize, CancellationToken ct);
    Task<Class?> GetClassAsync(int id, CancellationToken ct);
    Task<Class> GetClassDetailAsync(int id, CancellationToken ct);
    Task<Class> GetClassWithRoomAndCoachAsync(int id, CancellationToken ct);
    Task<ClassResources?> GetClassResourcesAsync(int id, CancellationToken ct);
    void AddClass(Class entity);
    Task<int> GetMaxFutureBookingsPerSessionAsync(int classId, DateOnly today, TimeOnly now, CancellationToken ct);
    Task<bool> ClassHasSessionsAsync(int classId, CancellationToken ct);
    Task<bool> HasFutureSessionsOutsideRangeAsync(int classId, DateOnly today, TimeOnly now, DateOnly start, DateOnly end, CancellationToken ct);
    Task<List<ClassSession>> GetFutureScheduledSessionsAsync(int classId, DateOnly today, TimeOnly now, bool includeRoom, CancellationToken ct);
    Task<List<DateOnly>> GetExistingSessionDatesAsync(int classId, List<DateOnly> dates, TimeOnly startTime, CancellationToken ct);
    void AddSessions(IEnumerable<ClassSession> sessions);

    // sessions
    Task<(List<SessionRow> Items, int Total)> ListOpenSessionsAsync(int? sportId, DateOnly from, DateOnly to, int? coachId, Level? level,
        int? classId, int page, int pageSize, CancellationToken ct);
    Task<SessionRow> GetSessionRowAsync(int id, CancellationToken ct);
    Task<SessionResources?> GetSessionResourcesAsync(int sessionId, CancellationToken ct);
    Task<List<int>> GetConfirmedMemberIdsAsync(int sessionId, CancellationToken ct);
    Task<ClassSession> GetSessionWithClassCoachAndRoomAsync(int id, CancellationToken ct);
    Task<ClassSession> GetSessionWithClassAsync(int id, CancellationToken ct);
    Task<int?> GetSessionCoachIdAsync(int sessionId, CancellationToken ct);
    Task<List<ClassBooking>> GetConfirmedBookingsAsync(int sessionId, CancellationToken ct);
    Task<ClassSession?> GetSessionForBookingAsync(int sessionId, CancellationToken ct);

    // class bookings
    Task<bool> HasConfirmedBookingAsync(int sessionId, int memberId, CancellationToken ct);
    Task<int> CountConfirmedBookingsAsync(int sessionId, CancellationToken ct);
    void AddBooking(ClassBooking booking);
    Task<BookingKey?> GetBookingKeyAsync(int bookingId, CancellationToken ct);
    Task<ClassBooking> GetBookingWithSessionAsync(int bookingId, CancellationToken ct);
    Task<bool> HasAttendedAsync(int sessionId, int memberId, CancellationToken ct);
}

public class ClassRepository(AppDbContext db) : IClassRepository
{
    public async Task<(List<Class> Items, int Total)> ListClassesAsync(int page, int pageSize, CancellationToken ct)
    {
        var query = db.Classes.AsNoTracking();
        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Include(x => x.Sport).Include(x => x.Room).Include(x => x.Coach).ToListAsync(ct);
        return (items, total);
    }

    public Task<Class?> GetClassAsync(int id, CancellationToken ct) => db.Classes.FirstOrDefaultAsync(x => x.Id == id, ct);

    public Task<Class> GetClassDetailAsync(int id, CancellationToken ct) => db.Classes.AsNoTracking()
        .Include(x => x.Sport).Include(x => x.Room).Include(x => x.Coach).SingleAsync(x => x.Id == id, ct);

    public Task<Class> GetClassWithRoomAndCoachAsync(int id, CancellationToken ct) =>
        db.Classes.Include(x => x.Room).Include(x => x.Coach).FirstAsync(x => x.Id == id, ct);

    public Task<ClassResources?> GetClassResourcesAsync(int id, CancellationToken ct) => db.Classes.AsNoTracking().Where(x => x.Id == id)
        .Select(x => new ClassResources(x.SportId, x.RoomId, x.CoachId)).FirstOrDefaultAsync(ct);

    public void AddClass(Class entity) => db.Classes.Add(entity);

    public Task<int> GetMaxFutureBookingsPerSessionAsync(int classId, DateOnly today, TimeOnly now, CancellationToken ct) =>
        db.ClassBookings.Where(x => x.Session.ClassId == classId && x.Status == BookingStatus.Confirmed &&
                (x.Session.Date > today || (x.Session.Date == today && x.Session.StartTime > now)))
            .GroupBy(x => x.SessionId).Select(x => x.Count()).DefaultIfEmpty().MaxAsync(ct);

    public Task<bool> ClassHasSessionsAsync(int classId, CancellationToken ct) => db.ClassSessions.AnyAsync(x => x.ClassId == classId, ct);

    public Task<bool> HasFutureSessionsOutsideRangeAsync(int classId, DateOnly today, TimeOnly now, DateOnly start, DateOnly end, CancellationToken ct) =>
        db.ClassSessions.AnyAsync(x => x.ClassId == classId && x.Status == SessionStatus.Scheduled &&
            (x.Date > today || (x.Date == today && x.StartTime > now)) && (x.Date < start || x.Date > end), ct);

    public Task<List<ClassSession>> GetFutureScheduledSessionsAsync(int classId, DateOnly today, TimeOnly now, bool includeRoom, CancellationToken ct)
    {
        var query = includeRoom ? db.ClassSessions.Include(x => x.Room) : db.ClassSessions.AsQueryable();
        return query.Where(x => x.ClassId == classId && x.Status == SessionStatus.Scheduled &&
            (x.Date > today || (x.Date == today && x.StartTime > now))).ToListAsync(ct);
    }

    public Task<List<DateOnly>> GetExistingSessionDatesAsync(int classId, List<DateOnly> dates, TimeOnly startTime, CancellationToken ct) =>
        db.ClassSessions.Where(x => x.ClassId == classId && dates.Contains(x.Date) && x.StartTime == startTime).Select(x => x.Date).ToListAsync(ct);

    public void AddSessions(IEnumerable<ClassSession> sessions) => db.ClassSessions.AddRange(sessions);

    public async Task<(List<SessionRow> Items, int Total)> ListOpenSessionsAsync(int? sportId, DateOnly from, DateOnly to, int? coachId, Level? level,
        int? classId, int page, int pageSize, CancellationToken ct)
    {
        var query = db.ClassSessions.AsNoTracking().Where(x => x.Date >= from && x.Date <= to && x.Status != SessionStatus.Cancelled &&
            x.Class.Status == ClassStatus.Open && x.CoachId != null && x.Coach!.IsActive &&
            x.Coach.CoachProfile!.Sports.Any(s => s.Id == x.Class.SportId) && x.Room.Status == RoomStatus.Active && x.Class.Sport.Status == SportStatus.Active);
        if (sportId is { } s) query = query.Where(x => x.Class.SportId == s);
        if (coachId is { } c) query = query.Where(x => x.CoachId == c);
        if (level is { } l) query = query.Where(x => x.Level == l);
        if (classId is { } cl) query = query.Where(x => x.ClassId == cl);
        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(x => x.Date).ThenBy(x => x.StartTime).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new SessionRow(x, x.Class, x.Class.Sport.Name, x.Room.Name, x.Coach!.FullName,
                db.ClassBookings.Count(b => b.SessionId == x.Id && b.Status == BookingStatus.Confirmed))).ToListAsync(ct);
        return (items, total);
    }

    public Task<SessionRow> GetSessionRowAsync(int id, CancellationToken ct) => db.ClassSessions.AsNoTracking().Where(x => x.Id == id)
        .Select(x => new SessionRow(x, x.Class, x.Class.Sport.Name, x.Room.Name, x.Coach == null ? null : x.Coach.FullName,
            db.ClassBookings.Count(b => b.SessionId == x.Id && b.Status == BookingStatus.Confirmed))).SingleAsync(ct);

    public Task<SessionResources?> GetSessionResourcesAsync(int sessionId, CancellationToken ct) => db.ClassSessions.AsNoTracking()
        .Where(x => x.Id == sessionId).Select(x => new SessionResources(x.ClassId, x.Class.SportId, x.RoomId, x.CoachId)).FirstOrDefaultAsync(ct);

    public Task<List<int>> GetConfirmedMemberIdsAsync(int sessionId, CancellationToken ct) =>
        db.ClassBookings.Where(x => x.SessionId == sessionId && x.Status == BookingStatus.Confirmed).Select(x => x.MemberId).ToListAsync(ct);

    public Task<ClassSession> GetSessionWithClassCoachAndRoomAsync(int id, CancellationToken ct) =>
        db.ClassSessions.Include(x => x.Class).Include(x => x.Coach).Include(x => x.Room).FirstAsync(x => x.Id == id, ct);

    public Task<ClassSession> GetSessionWithClassAsync(int id, CancellationToken ct) =>
        db.ClassSessions.Include(x => x.Class).FirstAsync(x => x.Id == id, ct);

    public Task<int?> GetSessionCoachIdAsync(int sessionId, CancellationToken ct) =>
        db.ClassSessions.Where(x => x.Id == sessionId).Select(x => x.CoachId).SingleAsync(ct);

    public Task<List<ClassBooking>> GetConfirmedBookingsAsync(int sessionId, CancellationToken ct) =>
        db.ClassBookings.Where(x => x.SessionId == sessionId && x.Status == BookingStatus.Confirmed).ToListAsync(ct);

    public Task<ClassSession?> GetSessionForBookingAsync(int sessionId, CancellationToken ct) =>
        db.ClassSessions.Include(x => x.Room).Include(x => x.Coach).Include(x => x.Class).ThenInclude(x => x.Sport)
            .FirstOrDefaultAsync(x => x.Id == sessionId, ct);

    public Task<bool> HasConfirmedBookingAsync(int sessionId, int memberId, CancellationToken ct) =>
        db.ClassBookings.AnyAsync(x => x.SessionId == sessionId && x.MemberId == memberId && x.Status == BookingStatus.Confirmed, ct);

    public Task<int> CountConfirmedBookingsAsync(int sessionId, CancellationToken ct) =>
        db.ClassBookings.CountAsync(x => x.SessionId == sessionId && x.Status == BookingStatus.Confirmed, ct);

    public void AddBooking(ClassBooking booking) => db.ClassBookings.Add(booking);

    public Task<BookingKey?> GetBookingKeyAsync(int bookingId, CancellationToken ct) => db.ClassBookings.AsNoTracking().Where(x => x.Id == bookingId)
        .Select(x => new BookingKey(x.MemberId, x.SessionId)).FirstOrDefaultAsync(ct);

    public Task<ClassBooking> GetBookingWithSessionAsync(int bookingId, CancellationToken ct) =>
        db.ClassBookings.Include(x => x.Session).FirstAsync(x => x.Id == bookingId, ct);

    public Task<bool> HasAttendedAsync(int sessionId, int memberId, CancellationToken ct) =>
        db.Attendances.AnyAsync(x => x.SessionId == sessionId && x.MemberId == memberId &&
            (x.Status == AttendanceStatus.Present || x.Status == AttendanceStatus.Late), ct);
}
