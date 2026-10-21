using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

public record AttendedRow(int ReferenceId, string Title, DateOnly Date, TimeOnly EndTime, AttendanceStatus? Status);
public record CoachSessionRow(ClassSession Session, string Sport, string Room, int Participants,
    AttendanceStatus? CoachAttendanceStatus, DateTime? CoachCheckedInAt);
public record RosterRow(int MemberId, string FullName, string Email, int BookingId, AttendanceStatus? Status,
    DateOnly Date, TimeOnly EndTime, DateTime? CheckedInAt, AttendanceSource? Source, bool LegacyFinalized);
public record ClassCheckInBookingRow(int BookingId, int SubscriptionId, int SportId);

// Schedules and attendance reads/writes (ATT-01, BK-03, CLS-06).
public interface IAttendanceRepository
{
    Task<List<ClassBooking>> GetMemberClassBookingsAsync(int memberId, DateOnly from, DateOnly to, bool includeCancelled, CancellationToken ct);
    Task<List<PtBooking>> GetMemberPtBookingsAsync(int memberId, DateOnly from, DateOnly to, bool includeCancelled, CancellationToken ct);
    Task<Dictionary<int, AttendanceStatus>> GetClassAttendanceMapAsync(int memberId, List<int> sessionIds, CancellationToken ct);
    Task<Dictionary<int, AttendanceStatus>> GetPtAttendanceMapAsync(int memberId, List<int> bookingIds, CancellationToken ct);
    Task<List<AttendedRow>> GetFinalizedClassAttendanceAsync(int memberId, DateOnly from, DateOnly toExclusive, DateOnly today, CancellationToken ct);
    Task<List<AttendedRow>> GetFinalizedPtAttendanceAsync(int memberId, DateOnly from, DateOnly toExclusive, DateOnly today, CancellationToken ct);
    Task<List<CoachSessionRow>> GetCoachClassSessionsAsync(int coachId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<List<PtBooking>> GetCoachPtBookingsAsync(int coachId, DateOnly from, DateOnly to, CancellationToken ct);

    Task<int?> GetClassSessionCoachAsync(int sessionId, CancellationToken ct);
    Task<int?> GetPtBookingCoachAsync(int bookingId, CancellationToken ct);
    Task<ClassSession?> FindSessionWithClassAsync(int sessionId, CancellationToken ct);
    Task<PtBooking?> FindPtBookingAsync(int bookingId, CancellationToken ct);
    Task<ClassCheckInBookingRow?> FindConfirmedClassBookingAsync(int sessionId, int memberId, CancellationToken ct);

    Task<Attendance?> GetClassAttendanceAsync(int sessionId, int memberId, CancellationToken ct);
    Task<CoachSessionAttendance?> GetCoachAttendanceAsync(int sessionId, int coachId, CancellationToken ct);
    Task<Attendance?> GetPtAttendanceAsync(int bookingId, int memberId, CancellationToken ct);
    void AddAttendance(Attendance attendance);
    void AddCoachAttendance(CoachSessionAttendance attendance);

    Task<List<RosterRow>> GetClassRosterAsync(int sessionId, CancellationToken ct);
    Task<List<RosterRow>> GetPtRosterAsync(int bookingId, CancellationToken ct);
}

public class AttendanceRepository(AppDbContext db) : IAttendanceRepository
{
    public Task<List<ClassBooking>> GetMemberClassBookingsAsync(int memberId, DateOnly from, DateOnly to, bool includeCancelled, CancellationToken ct)
    {
        var query = db.ClassBookings.AsNoTracking().Where(x => x.MemberId == memberId && x.Session.Date >= from && x.Session.Date <= to);
        if (!includeCancelled) query = query.Where(x => x.Status != BookingStatus.Cancelled);
        return query.Include(x => x.Session).ThenInclude(x => x.Class).ThenInclude(x => x.Sport)
            .Include(x => x.Session).ThenInclude(x => x.Room).Include(x => x.Session).ThenInclude(x => x.Coach).ToListAsync(ct);
    }

    public Task<List<PtBooking>> GetMemberPtBookingsAsync(int memberId, DateOnly from, DateOnly to, bool includeCancelled, CancellationToken ct)
    {
        var query = db.PtBookings.AsNoTracking().Where(x => x.MemberId == memberId && x.Date >= from && x.Date <= to);
        if (!includeCancelled) query = query.Where(x => x.Status != BookingStatus.Cancelled);
        return query.Include(x => x.Sport).Include(x => x.Room).Include(x => x.Coach).ToListAsync(ct);
    }

    public Task<Dictionary<int, AttendanceStatus>> GetClassAttendanceMapAsync(int memberId, List<int> sessionIds, CancellationToken ct) =>
        db.Attendances.AsNoTracking()
            .Where(a => a.MemberId == memberId && a.SessionId != null && sessionIds.Contains(a.SessionId.Value))
            .ToDictionaryAsync(a => a.SessionId!.Value, a => a.Status, ct);

    public Task<Dictionary<int, AttendanceStatus>> GetPtAttendanceMapAsync(int memberId, List<int> bookingIds, CancellationToken ct) =>
        db.Attendances.AsNoTracking()
            .Where(a => a.MemberId == memberId && a.PtBookingId != null && bookingIds.Contains(a.PtBookingId.Value))
            .ToDictionaryAsync(a => a.PtBookingId!.Value, a => a.Status, ct);

    public Task<List<AttendedRow>> GetFinalizedClassAttendanceAsync(int memberId, DateOnly from, DateOnly toExclusive, DateOnly today, CancellationToken ct) =>
        db.ClassBookings.AsNoTracking().Where(x => x.MemberId == memberId && x.Status == BookingStatus.Confirmed &&
                x.Session.Status != SessionStatus.Cancelled &&
                x.Session.Date >= from && x.Session.Date < toExclusive && x.Session.Date <= today)
            .Select(x => new AttendedRow(x.SessionId, x.Session.ClassName, x.Session.Date, x.Session.EndTime,
                db.Attendances.Where(a => a.SessionId == x.SessionId && a.MemberId == memberId)
                    .Select(a => (AttendanceStatus?)a.Status).FirstOrDefault())).ToListAsync(ct);

    public Task<List<AttendedRow>> GetFinalizedPtAttendanceAsync(int memberId, DateOnly from, DateOnly toExclusive, DateOnly today, CancellationToken ct) =>
        db.PtBookings.AsNoTracking().Where(x => x.MemberId == memberId && x.Status != BookingStatus.Cancelled &&
                x.AttendanceFinalizedAt != null && x.Date >= from && x.Date < toExclusive && x.Date <= today)
            .Select(x => new AttendedRow(x.Id, "Personal Training", x.Date, x.EndTime,
                db.Attendances.Where(a => a.PtBookingId == x.Id && a.MemberId == memberId)
                    .Select(a => (AttendanceStatus?)a.Status).FirstOrDefault())).ToListAsync(ct);

    public Task<List<CoachSessionRow>> GetCoachClassSessionsAsync(int coachId, DateOnly from, DateOnly to, CancellationToken ct) =>
        db.ClassSessions.AsNoTracking().Where(x => x.CoachId == coachId && x.Date >= from && x.Date <= to)
            .Select(x => new CoachSessionRow(x, x.Class.Sport.Name, x.Room.Name,
                db.ClassBookings.Count(b => b.SessionId == x.Id && b.Status == BookingStatus.Confirmed),
                db.CoachSessionAttendances.Where(a => a.SessionId == x.Id && a.CoachId == coachId)
                    .Select(a => (AttendanceStatus?)a.Status).FirstOrDefault(),
                db.CoachSessionAttendances.Where(a => a.SessionId == x.Id && a.CoachId == coachId)
                    .Select(a => (DateTime?)a.CheckedInAt).FirstOrDefault())).ToListAsync(ct);

    public Task<List<PtBooking>> GetCoachPtBookingsAsync(int coachId, DateOnly from, DateOnly to, CancellationToken ct) =>
        db.PtBookings.AsNoTracking().Where(x => x.CoachId == coachId && x.Date >= from && x.Date <= to)
            .Include(x => x.Sport).Include(x => x.Room).ToListAsync(ct);

    public Task<int?> GetClassSessionCoachAsync(int sessionId, CancellationToken ct) =>
        db.ClassSessions.Where(x => x.Id == sessionId).Select(x => x.CoachId).FirstOrDefaultAsync(ct);

    public Task<int?> GetPtBookingCoachAsync(int bookingId, CancellationToken ct) =>
        db.PtBookings.Where(x => x.Id == bookingId).Select(x => (int?)x.CoachId).FirstOrDefaultAsync(ct);

    public Task<ClassSession?> FindSessionWithClassAsync(int sessionId, CancellationToken ct) =>
        db.ClassSessions.Include(x => x.Class).FirstOrDefaultAsync(x => x.Id == sessionId, ct);

    public Task<PtBooking?> FindPtBookingAsync(int bookingId, CancellationToken ct) => db.PtBookings.FirstOrDefaultAsync(x => x.Id == bookingId, ct);

    public Task<ClassCheckInBookingRow?> FindConfirmedClassBookingAsync(int sessionId, int memberId, CancellationToken ct) =>
        db.ClassBookings.AsNoTracking()
            .Where(x => x.SessionId == sessionId && x.MemberId == memberId && x.Status == BookingStatus.Confirmed)
            .Select(x => new ClassCheckInBookingRow(x.Id, x.SubscriptionId, x.Session.Class.SportId))
            .FirstOrDefaultAsync(ct);

    public Task<Attendance?> GetClassAttendanceAsync(int sessionId, int memberId, CancellationToken ct) =>
        db.Attendances.FirstOrDefaultAsync(x => x.SessionId == sessionId && x.MemberId == memberId, ct);

    public Task<CoachSessionAttendance?> GetCoachAttendanceAsync(int sessionId, int coachId, CancellationToken ct) =>
        db.CoachSessionAttendances.FirstOrDefaultAsync(x => x.SessionId == sessionId && x.CoachId == coachId, ct);

    public Task<Attendance?> GetPtAttendanceAsync(int bookingId, int memberId, CancellationToken ct) =>
        db.Attendances.FirstOrDefaultAsync(x => x.PtBookingId == bookingId && x.MemberId == memberId, ct);

    public void AddAttendance(Attendance attendance) => db.Attendances.Add(attendance);
    public void AddCoachAttendance(CoachSessionAttendance attendance) => db.CoachSessionAttendances.Add(attendance);

    public Task<List<RosterRow>> GetClassRosterAsync(int sessionId, CancellationToken ct) => db.ClassBookings.AsNoTracking()
        .Where(x => x.SessionId == sessionId && x.Status == BookingStatus.Confirmed)
        .OrderBy(x => x.Member.FullName).Select(x => new RosterRow(x.MemberId, x.Member.FullName, x.Member.Email, x.Id,
            db.Attendances.Where(a => a.SessionId == sessionId && a.MemberId == x.MemberId).Select(a => (AttendanceStatus?)a.Status).FirstOrDefault(),
            x.Session.Date, x.Session.EndTime,
            db.Attendances.Where(a => a.SessionId == sessionId && a.MemberId == x.MemberId).Select(a => (DateTime?)a.MarkedAt).FirstOrDefault(),
            db.Attendances.Where(a => a.SessionId == sessionId && a.MemberId == x.MemberId).Select(a => (AttendanceSource?)a.Source).FirstOrDefault(),
            x.Session.AttendanceFinalizedAt != null)).ToListAsync(ct);

    public Task<List<RosterRow>> GetPtRosterAsync(int bookingId, CancellationToken ct) => db.PtBookings.AsNoTracking()
        .Where(x => x.Id == bookingId && x.Status == BookingStatus.Confirmed)
        .Select(x => new RosterRow(x.MemberId, x.Member.FullName, x.Member.Email, x.Id,
            db.Attendances.Where(a => a.PtBookingId == bookingId && a.MemberId == x.MemberId).Select(a => (AttendanceStatus?)a.Status).FirstOrDefault(),
            x.Date, x.EndTime,
            db.Attendances.Where(a => a.PtBookingId == bookingId && a.MemberId == x.MemberId).Select(a => (DateTime?)a.MarkedAt).FirstOrDefault(),
            db.Attendances.Where(a => a.PtBookingId == bookingId && a.MemberId == x.MemberId).Select(a => (AttendanceSource?)a.Source).FirstOrDefault(),
            x.AttendanceFinalizedAt != null)).ToListAsync(ct);
}
