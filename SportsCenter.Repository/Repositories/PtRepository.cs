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
    void AddBooking(PtBooking booking);

    Task<int?> FindPtSubscriptionIdAsync(int memberId, int sportId, DateOnly date, CancellationToken ct);
    Task<int> ConsumeCreditAsync(int subscriptionId, CancellationToken ct);
    Task RefundCreditAsync(int subscriptionId, CancellationToken ct);
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

    public void AddBooking(PtBooking booking) => db.PtBookings.Add(booking);

    public Task<int?> FindPtSubscriptionIdAsync(int memberId, int sportId, DateOnly date, CancellationToken ct) =>
        db.MemberSubscriptions.AsNoTracking().Where(x => x.MemberId == memberId && x.Status == SubscriptionStatus.Active &&
                x.StartDate <= date && date <= x.EndDate && x.RemainingPtSessions > 0 && x.Plan.SportId == sportId && x.Plan.PtSessionCredits > 0 &&
                !db.Payments.Any(p => p.SubscriptionId == x.Id && p.Status == PaymentStatus.Pending))
            .OrderBy(x => x.EndDate).ThenBy(x => x.Id).Select(x => (int?)x.Id).FirstOrDefaultAsync(ct);

    // Atomic decrement; returns the number of rows changed (0 when another request used the last credit).
    public Task<int> ConsumeCreditAsync(int subscriptionId, CancellationToken ct) =>
        db.MemberSubscriptions.Where(x => x.Id == subscriptionId && x.RemainingPtSessions > 0)
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.RemainingPtSessions, s => s.RemainingPtSessions - 1), ct);

    public Task RefundCreditAsync(int subscriptionId, CancellationToken ct) =>
        db.MemberSubscriptions.Where(x => x.Id == subscriptionId)
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.RemainingPtSessions, s => s.RemainingPtSessions + 1), ct);
}
