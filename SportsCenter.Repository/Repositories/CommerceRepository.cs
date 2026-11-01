using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

// Materialized read models keep EF/provider details within the Repository project.
public record WalletBalanceRow(int MemberId, decimal Balance, decimal LedgerBalance);
public record PtPriceRow(int Id, string Name, decimal? PtSinglePrice);
public record CourseCatalogRow(int Id, string Name, string? Description, Level Level, int SportId, string Sport,
    int? CoachId, string? Coach, int RoomId, string Room, int Capacity, DateOnly StartDate, DateOnly EndDate,
    ClassStatus Status, decimal CourseFee, int ExpectedSessions, DateTime? EnrollmentClosesAt, DateTime? PublishedAt, int Enrolled,
    DateTime? CourseStartsAt, DateOnly? FirstSessionDate, DateOnly? LastSessionDate,
    SportStatus SportStatus, bool SportHasClasses)
{
    public int DurationWeeks => (int)Math.Ceiling((EndDate.DayNumber - StartDate.DayNumber + 1) / 7m);
    public decimal? SessionPrice => ExpectedSessions > 0 && CourseFee % ExpectedSessions == 0 ? CourseFee / ExpectedSessions : null;
}
public record CourseSessionRow(int Id, DateOnly Date, TimeOnly StartTime, TimeOnly EndTime, SessionStatus Status,
    int RoomId, string Room, int? CoachId, string? Coach, int? ReplacesSessionId);
public record PaidEnrollmentRow(int Id, int ClassId, string Course, int MemberId, string Member, EnrollmentStatus Status,
    int? OrderId, decimal PaidAmount, decimal RefundedAmount, DateTime EnrolledAt, DateTime? EnrollmentClosesAt);
public record CourseSupportRow(int Id, string Subject, int MemberId, string Member, string Description, SupportStatus Status, DateTime CreatedAt);

public interface ICommerceRepository
{
    void Add<T>(T entity) where T : class;
    Task<WalletBalanceRow> GetWalletBalanceAsync(int memberId, CancellationToken ct);
    Task<(List<WalletEntry> Items, int Total)> ListWalletEntriesAsync(int memberId, int page, CancellationToken ct);
    Task<bool> PaymentReferenceExistsAsync(string reference, CancellationToken ct);
    Task<bool> WalletReferenceExistsAsync(string reference, CancellationToken ct);
    Task<MemberWallet?> FindWalletAsync(int memberId, CancellationToken ct);
    Task<CommerceRequest?> FindRequestAsync(int actorId, string operation, string key, CancellationToken ct);
    Task<List<PtPriceRow>> ListPtPricesAsync(CancellationToken ct);
    Task<Sport?> FindPtSportAsync(int id, CancellationToken ct);
    Task<List<CourseCatalogRow>> ListCoursesAsync(bool includeUnpublished, CancellationToken ct);
    Task<List<CourseSessionRow>> ListCourseScheduleAsync(int courseId, CancellationToken ct);
    Task<List<PaidEnrollmentRow>> ListPaidEnrollmentsAsync(int? memberId, int? courseId, CancellationToken ct);
    Task<bool> CourseHasSalesAsync(int courseId, CancellationToken ct);
    Task<Class?> FindCourseAsync(int courseId, bool tracking, CancellationToken ct);
    Task<ClassSession?> FindFirstActiveCourseSessionAsync(int courseId, CancellationToken ct);
    Task<ClassEnrollment?> FindMemberEnrollmentAsync(int courseId, int memberId, CancellationToken ct);
    Task<int> CountConfirmedPaidEnrollmentsAsync(int courseId, CancellationToken ct);
    Task<int> CountConfirmedSessionBookingsAsync(int sessionId, CancellationToken ct);
    Task<ClassEnrollment?> FindPaidEnrollmentAsync(int enrollmentId, CancellationToken ct);
    Task<ClassEnrollment> GetEnrollmentAsync(int enrollmentId, CancellationToken ct);
    Task<bool> EnrollmentHasAttendanceAsync(int enrollmentId, CancellationToken ct);
    Task<List<CourseEntitlement>> ListUnrefundedOrderUnitsAsync(int? orderId, CancellationToken ct);
    Task<List<CourseSupportRow>> ListSupportRequestsAsync(string prefix, CancellationToken ct);
    Task<List<Attendance>> ListCourseAttendanceAsync(int courseId, CancellationToken ct);
    Task<List<ClassEnrollment>> ListConfirmedPaidEnrollmentsAsync(int courseId, CancellationToken ct);
    Task<List<CourseEntitlement>> ListCurrentCourseUnitsAsync(int courseId, CancellationToken ct);
    Task<List<ClassSession>> ListScheduledCourseSessionsAsync(int courseId, CancellationToken ct);
    Task<ClassSession?> FindCourseSessionAsync(int courseId, int sessionId, CancellationToken ct);
    Task<bool> SessionHasAttendanceAsync(int sessionId, CancellationToken ct);
    Task<List<ClassBooking>> ListConfirmedSessionBookingsAsync(int sessionId, CancellationToken ct);
    Task<List<CourseEntitlement>> ListCurrentSessionUnitsAsync(int sessionId, CancellationToken ct);
    Task<Room?> FindRoomAsync(int roomId, CancellationToken ct);
    Task<MembershipPlan?> FindPlanAsync(int planId, CancellationToken ct);
    Task<MemberSubscription?> FindPurchasedSubscriptionAsync(int subscriptionId, CancellationToken ct);
    Task<MemberSubscription> GetSubscriptionAsync(int subscriptionId, CancellationToken ct);
    Task<CommerceOrder> GetOrderAsync(int? orderId, CancellationToken ct);
    Task<bool> SubscriptionHasPtBookingsAsync(int subscriptionId, CancellationToken ct);
    Task<List<ClassBooking>> ListConfirmedEnrollmentBookingsAsync(int enrollmentId, CancellationToken ct);
    Task<List<ClassSession>> ListCourseSessionsForLockAsync(int courseId, CancellationToken ct);
    Task<List<int>> ListConfirmedCourseMembersAsync(int courseId, CancellationToken ct);
    Task<List<ClassSession>> ListActiveCourseSessionsWithRoomsAsync(int courseId, CancellationToken ct);
    Task<Sport> GetSportAsync(int sportId, CancellationToken ct);
    Task<Sport?> GetSportOrDefaultAsync(int sportId, CancellationToken ct);
}

// Shares the existing scoped DbContext and scheduling transaction. No business policy or independent commits.
public sealed class CommerceRepository(AppDbContext db) : ICommerceRepository
{
    public void Add<T>(T entity) where T : class => db.Set<T>().Add(entity);

    public Task<WalletBalanceRow> GetWalletBalanceAsync(int memberId, CancellationToken ct) =>
        db.Users.Where(x => x.Id == memberId).Select(x => new WalletBalanceRow(memberId,
            db.MemberWallets.Where(w => w.MemberId == memberId).Select(w => w.Balance).FirstOrDefault(),
            db.WalletEntries.Where(e => e.MemberId == memberId).Sum(e => (decimal?)e.Amount) ?? 0)).SingleAsync(ct);

    public async Task<(List<WalletEntry> Items, int Total)> ListWalletEntriesAsync(int memberId, int page, CancellationToken ct)
    {
        var query = db.WalletEntries.AsNoTracking().Where(x => x.MemberId == memberId);
        var items = await query.OrderByDescending(x => x.Id).Skip((page - 1) * 20).Take(20).ToListAsync(ct);
        return (items, await query.CountAsync(ct));
    }
    public Task<bool> PaymentReferenceExistsAsync(string reference, CancellationToken ct) =>
        db.Payments.AnyAsync(x => x.ProviderReference == reference, ct);
    public Task<bool> WalletReferenceExistsAsync(string reference, CancellationToken ct) =>
        db.WalletEntries.AnyAsync(x => x.Reference == reference, ct);
    public Task<MemberWallet?> FindWalletAsync(int memberId, CancellationToken ct) =>
        db.MemberWallets.SingleOrDefaultAsync(x => x.MemberId == memberId, ct);
    public Task<CommerceRequest?> FindRequestAsync(int actorId, string operation, string key, CancellationToken ct) =>
        db.CommerceRequests.AsNoTracking().SingleOrDefaultAsync(x => x.ActorId == actorId && x.Operation == operation && x.Key == key, ct);

    public Task<List<PtPriceRow>> ListPtPricesAsync(CancellationToken ct) =>
        db.Sports.AsNoTracking().Where(x => x.HasPt && x.Status == SportStatus.Active)
            .Select(x => new PtPriceRow(x.Id, x.Name, x.PtSinglePrice)).ToListAsync(ct);
    public Task<Sport?> FindPtSportAsync(int id, CancellationToken ct) =>
        db.Sports.SingleOrDefaultAsync(x => x.Id == id && x.HasPt, ct);

    public Task<List<CourseCatalogRow>> ListCoursesAsync(bool includeUnpublished, CancellationToken ct)
    {
        var query = db.Classes.AsNoTracking().Where(x => x.BookingMode == ClassBookingMode.CourseEnrollment);
        if (!includeUnpublished) query = query.Where(x => x.PublishedAt != null && x.Status != ClassStatus.Draft);
        return query.OrderByDescending(x => x.Id).Take(500).Select(x => new CourseCatalogRow(
            x.Id, x.Name, x.Description, x.Level, x.SportId, x.Sport.Name, x.CoachId, x.Coach!.FullName,
            x.RoomId, x.Room.Name, x.Capacity, x.StartDate, x.EndDate, x.Status, x.CourseFee, x.ExpectedSessions,
            x.EnrollmentClosesAt, x.PublishedAt,
            x.Enrollments.Count(e => e.OrderId != null && e.Status == EnrollmentStatus.Confirmed), x.CourseStartsAt,
            x.Sessions.Where(s => s.Status != SessionStatus.Cancelled).Min(s => (DateOnly?)s.Date),
            x.Sessions.Where(s => s.Status != SessionStatus.Cancelled).Max(s => (DateOnly?)s.Date),
            x.Sport.Status, x.Sport.HasClasses)).ToListAsync(ct);
    }
    public Task<List<CourseSessionRow>> ListCourseScheduleAsync(int courseId, CancellationToken ct) =>
        db.ClassSessions.AsNoTracking().Where(x => x.ClassId == courseId).OrderBy(x => x.Date).ThenBy(x => x.StartTime)
            .Select(x => new CourseSessionRow(x.Id, x.Date, x.StartTime, x.EndTime, x.Status, x.RoomId, x.Room.Name,
                x.CoachId, x.Coach!.FullName, x.ReplacesSessionId)).ToListAsync(ct);
    public Task<List<PaidEnrollmentRow>> ListPaidEnrollmentsAsync(int? memberId, int? courseId, CancellationToken ct)
    {
        var query = db.ClassEnrollments.AsNoTracking().Where(x => x.OrderId != null);
        if (memberId != null) query = query.Where(x => x.MemberId == memberId);
        if (courseId != null) query = query.Where(x => x.ClassId == courseId);
        return query.OrderByDescending(x => x.Id).Take(500).Select(x => new PaidEnrollmentRow(x.Id, x.ClassId, x.Class.Name,
            x.MemberId, x.Member.FullName, x.Status, x.OrderId, x.Order!.PaidAmount, x.Order.RefundedAmount, x.EnrolledAt, x.Class.EnrollmentClosesAt)).ToListAsync(ct);
    }
    public Task<bool> CourseHasSalesAsync(int courseId, CancellationToken ct) =>
        db.ClassEnrollments.AnyAsync(x => x.ClassId == courseId && x.OrderId != null, ct);
    public Task<Class?> FindCourseAsync(int courseId, bool tracking, CancellationToken ct)
    {
        var query = tracking ? db.Classes.AsQueryable() : db.Classes.AsNoTracking();
        return query.SingleOrDefaultAsync(x => x.Id == courseId && x.BookingMode == ClassBookingMode.CourseEnrollment, ct);
    }
    public Task<ClassSession?> FindFirstActiveCourseSessionAsync(int courseId, CancellationToken ct) =>
        db.ClassSessions.Where(x => x.ClassId == courseId && x.Status != SessionStatus.Cancelled)
            .OrderBy(x => x.Date).ThenBy(x => x.StartTime).FirstOrDefaultAsync(ct);
    public Task<ClassEnrollment?> FindMemberEnrollmentAsync(int courseId, int memberId, CancellationToken ct) =>
        db.ClassEnrollments.SingleOrDefaultAsync(x => x.ClassId == courseId && x.MemberId == memberId, ct);
    public Task<int> CountConfirmedPaidEnrollmentsAsync(int courseId, CancellationToken ct) =>
        db.ClassEnrollments.CountAsync(x => x.ClassId == courseId && x.OrderId != null && x.Status == EnrollmentStatus.Confirmed, ct);
    public Task<int> CountConfirmedSessionBookingsAsync(int sessionId, CancellationToken ct) =>
        db.ClassBookings.CountAsync(x => x.SessionId == sessionId && x.Status == BookingStatus.Confirmed, ct);
    public Task<ClassEnrollment?> FindPaidEnrollmentAsync(int enrollmentId, CancellationToken ct) =>
        db.ClassEnrollments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == enrollmentId && x.OrderId != null, ct);
    public Task<ClassEnrollment> GetEnrollmentAsync(int enrollmentId, CancellationToken ct) =>
        db.ClassEnrollments.SingleAsync(x => x.Id == enrollmentId, ct);
    public Task<bool> EnrollmentHasAttendanceAsync(int enrollmentId, CancellationToken ct) =>
        db.Attendances.AnyAsync(x => db.ClassBookings.Any(b => b.EnrollmentId == enrollmentId && b.SessionId == x.SessionId && b.MemberId == x.MemberId), ct);
    public Task<List<CourseEntitlement>> ListUnrefundedOrderUnitsAsync(int? orderId, CancellationToken ct) =>
        db.CourseEntitlements.Where(x => x.OrderId == orderId && !x.Refunded).ToListAsync(ct);
    public Task<List<CourseSupportRow>> ListSupportRequestsAsync(string prefix, CancellationToken ct) =>
        db.SupportRequests.AsNoTracking().Where(x => x.Subject.StartsWith(prefix)).OrderByDescending(x => x.Id)
            .Select(x => new CourseSupportRow(x.Id, x.Subject, x.MemberId, x.Member.FullName, x.Description, x.Status, x.CreatedAt)).ToListAsync(ct);
    public Task<List<Attendance>> ListCourseAttendanceAsync(int courseId, CancellationToken ct) =>
        db.Attendances.Include(x => x.Session).Where(x => x.Session != null && x.Session.ClassId == courseId).ToListAsync(ct);
    public Task<List<ClassEnrollment>> ListConfirmedPaidEnrollmentsAsync(int courseId, CancellationToken ct) =>
        db.ClassEnrollments.Where(x => x.ClassId == courseId && x.OrderId != null && x.Status == EnrollmentStatus.Confirmed).ToListAsync(ct);
    public Task<List<CourseEntitlement>> ListCurrentCourseUnitsAsync(int courseId, CancellationToken ct) =>
        db.CourseEntitlements.Include(x => x.CurrentSession).Where(x => x.Enrollment.ClassId == courseId && !x.Refunded &&
            x.Enrollment.Status == EnrollmentStatus.Confirmed && x.OrderId == x.Enrollment.OrderId).ToListAsync(ct);
    public Task<List<ClassSession>> ListScheduledCourseSessionsAsync(int courseId, CancellationToken ct) =>
        db.ClassSessions.Where(x => x.ClassId == courseId && x.Status == SessionStatus.Scheduled).ToListAsync(ct);
    public Task<ClassSession?> FindCourseSessionAsync(int courseId, int sessionId, CancellationToken ct) =>
        db.ClassSessions.SingleOrDefaultAsync(x => x.Id == sessionId && x.ClassId == courseId, ct);
    public Task<bool> SessionHasAttendanceAsync(int sessionId, CancellationToken ct) =>
        db.Attendances.AnyAsync(x => x.SessionId == sessionId, ct);
    public Task<List<ClassBooking>> ListConfirmedSessionBookingsAsync(int sessionId, CancellationToken ct) =>
        db.ClassBookings.Where(x => x.SessionId == sessionId && x.Status == BookingStatus.Confirmed).ToListAsync(ct);
    public Task<List<CourseEntitlement>> ListCurrentSessionUnitsAsync(int sessionId, CancellationToken ct) =>
        db.CourseEntitlements.Where(x => x.CurrentSessionId == sessionId && !x.Refunded &&
            x.Enrollment.Status == EnrollmentStatus.Confirmed && x.OrderId == x.Enrollment.OrderId).ToListAsync(ct);
    public Task<Room?> FindRoomAsync(int roomId, CancellationToken ct) =>
        db.Rooms.SingleOrDefaultAsync(x => x.Id == roomId, ct);
    public Task<MembershipPlan?> FindPlanAsync(int planId, CancellationToken ct) =>
        db.MembershipPlans.SingleOrDefaultAsync(x => x.Id == planId, ct);
    public Task<MemberSubscription?> FindPurchasedSubscriptionAsync(int subscriptionId, CancellationToken ct) =>
        db.MemberSubscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == subscriptionId && x.PurchaseOrderId != null, ct);
    public Task<MemberSubscription> GetSubscriptionAsync(int subscriptionId, CancellationToken ct) =>
        db.MemberSubscriptions.SingleAsync(x => x.Id == subscriptionId, ct);
    public Task<CommerceOrder> GetOrderAsync(int? orderId, CancellationToken ct) =>
        db.CommerceOrders.SingleAsync(x => x.Id == orderId, ct);
    public Task<bool> SubscriptionHasPtBookingsAsync(int subscriptionId, CancellationToken ct) =>
        db.PtBookings.AnyAsync(x => x.SubscriptionId == subscriptionId, ct);
    public Task<List<ClassBooking>> ListConfirmedEnrollmentBookingsAsync(int enrollmentId, CancellationToken ct) =>
        db.ClassBookings.Include(x => x.Session).Where(x => x.EnrollmentId == enrollmentId && x.Status == BookingStatus.Confirmed).ToListAsync(ct);
    public Task<List<ClassSession>> ListCourseSessionsForLockAsync(int courseId, CancellationToken ct) =>
        db.ClassSessions.AsNoTracking().Where(x => x.ClassId == courseId).ToListAsync(ct);
    public Task<List<int>> ListConfirmedCourseMembersAsync(int courseId, CancellationToken ct) =>
        db.ClassEnrollments.Where(x => x.ClassId == courseId && x.Status == EnrollmentStatus.Confirmed).Select(x => x.MemberId).ToListAsync(ct);
    public Task<List<ClassSession>> ListActiveCourseSessionsWithRoomsAsync(int courseId, CancellationToken ct) =>
        db.ClassSessions.Include(x => x.Room).Where(x => x.ClassId == courseId && x.Status != SessionStatus.Cancelled)
            .OrderBy(x => x.Date).ThenBy(x => x.StartTime).ThenBy(x => x.Id).ToListAsync(ct);
    public Task<Sport> GetSportAsync(int sportId, CancellationToken ct) =>
        db.Sports.SingleAsync(x => x.Id == sportId, ct);
    public Task<Sport?> GetSportOrDefaultAsync(int sportId, CancellationToken ct) =>
        db.Sports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sportId, ct);
}
