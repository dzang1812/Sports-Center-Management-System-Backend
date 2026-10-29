using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

public record BusySlot(DateOnly Date, TimeOnly StartTime, TimeOnly EndTime);
public record PtBookingKey(int SportId, int MemberId, int CoachId);

// Personal training: coach availability, PT bookings and the PT credit on a member's subscription (PT-*).
public interface IPtRepository
{
    Task<List<CoachAvailability>> GetActiveAvailabilityAsync(int coachId, CancellationToken ct);
    Task<List<CoachAvailability>> GetActiveAvailabilityForUpdateAsync(int coachId, CancellationToken ct);
    Task<List<CoachAvailability>> GetActiveAvailabilityForSportAsync(int coachId, int sportId, CancellationToken ct);
    void AddAvailabilities(IEnumerable<CoachAvailability> items);
    Task<bool> IsAvailableAsync(int coachId, int sportId, DateOnly date, TimeOnly start, TimeOnly end, CancellationToken ct);

    Task<List<PtBooking>> GetFutureConfirmedBookingsAsync(int coachId, DateOnly today, CancellationToken ct);
    Task<List<BusySlot>> GetCoachClassBusyAsync(int coachId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<List<BusySlot>> GetCoachPtBusyAsync(int coachId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<PtBookingKey?> GetBookingKeyAsync(int bookingId, CancellationToken ct);
    Task<PtBooking> GetBookingAsync(int bookingId, CancellationToken ct);
    Task<bool> HasAttendanceAsync(int bookingId, CancellationToken ct);
    Task<(List<PtBooking> Items, int Total)> ListBookingsAsync(DateOnly from, DateOnly to, int? memberId,
        int? coachId, int? sportId, BookingStatus? status, int page, int pageSize, CancellationToken ct);
    void AddBooking(PtBooking booking);

}

public class PtRepository(AppDbContext db) : IPtRepository
{
    public Task<List<CoachAvailability>> GetActiveAvailabilityAsync(int coachId, CancellationToken ct) => db.CoachAvailabilities.AsNoTracking()
        .Where(x => x.CoachId == coachId && x.IsActive).Include(x => x.Sport).OrderBy(x => x.DayOfWeek).ThenBy(x => x.StartTime).ToListAsync(ct);

    public Task<List<CoachAvailability>> GetActiveAvailabilityForUpdateAsync(int coachId, CancellationToken ct) =>
        db.CoachAvailabilities.Where(x => x.CoachId == coachId && x.IsActive).ToListAsync(ct);

    public Task<List<CoachAvailability>> GetActiveAvailabilityForSportAsync(int coachId, int sportId, CancellationToken ct) =>
        db.CoachAvailabilities.AsNoTracking().Where(x => x.CoachId == coachId && x.SportId == sportId && x.IsActive).ToListAsync(ct);

    public void AddAvailabilities(IEnumerable<CoachAvailability> items) => db.CoachAvailabilities.AddRange(items);

    public Task<bool> IsAvailableAsync(int coachId, int sportId, DateOnly date, TimeOnly start, TimeOnly end, CancellationToken ct) =>
        db.CoachAvailabilities.AnyAsync(x => x.CoachId == coachId && x.SportId == sportId && x.IsActive &&
            x.DayOfWeek == date.DayOfWeek && x.StartTime <= start && x.EndTime >= end, ct);

    public Task<List<PtBooking>> GetFutureConfirmedBookingsAsync(int coachId, DateOnly today, CancellationToken ct) =>
        db.PtBookings.Where(x => x.CoachId == coachId && x.Status == BookingStatus.Confirmed && x.Date >= today).ToListAsync(ct);

    public Task<List<BusySlot>> GetCoachClassBusyAsync(int coachId, DateOnly from, DateOnly to, CancellationToken ct) =>
        db.ClassSessions.AsNoTracking().Where(x => x.CoachId == coachId && x.Status != SessionStatus.Cancelled && x.Date >= from && x.Date <= to)
            .Select(x => new BusySlot(x.Date, x.StartTime, x.EndTime)).ToListAsync(ct);

    public Task<List<BusySlot>> GetCoachPtBusyAsync(int coachId, DateOnly from, DateOnly to, CancellationToken ct) =>
        db.PtBookings.AsNoTracking().Where(x => x.CoachId == coachId && x.Status == BookingStatus.Confirmed && x.Date >= from && x.Date <= to)
            .Select(x => new BusySlot(x.Date, x.StartTime, x.EndTime)).ToListAsync(ct);

    public Task<PtBookingKey?> GetBookingKeyAsync(int bookingId, CancellationToken ct) => db.PtBookings.AsNoTracking().Where(x => x.Id == bookingId)
        .Select(x => new PtBookingKey(x.SportId, x.MemberId, x.CoachId)).FirstOrDefaultAsync(ct);

    public Task<PtBooking> GetBookingAsync(int bookingId, CancellationToken ct) => db.PtBookings.FirstAsync(x => x.Id == bookingId, ct);

    public Task<bool> HasAttendanceAsync(int bookingId, CancellationToken ct) =>
        db.Attendances.AnyAsync(x => x.PtBookingId == bookingId, ct);

    public async Task<(List<PtBooking> Items, int Total)> ListBookingsAsync(DateOnly from, DateOnly to, int? memberId,
        int? coachId, int? sportId, BookingStatus? status, int page, int pageSize, CancellationToken ct)
    {
        var query = db.PtBookings.AsNoTracking().Where(x => x.Date >= from && x.Date <= to);
        if (memberId is { } member) query = query.Where(x => x.MemberId == member);
        if (coachId is { } coach) query = query.Where(x => x.CoachId == coach);
        if (sportId is { } sport) query = query.Where(x => x.SportId == sport);
        if (status is { } state) query = query.Where(x => x.Status == state);
        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(x => x.Date).ThenBy(x => x.StartTime).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Include(x => x.Member).Include(x => x.Coach).Include(x => x.Sport).Include(x => x.Room).ToListAsync(ct);
        return (items, total);
    }

    public void AddBooking(PtBooking booking) => db.PtBookings.Add(booking);

}
