using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Services.Scheduling;

public record AvailabilityItem(int Id, int SportId, string Sport, DayOfWeek DayOfWeek, TimeOnly StartTime, TimeOnly EndTime);
public record AvailabilityRange(int SportId, DayOfWeek DayOfWeek, TimeOnly StartTime, TimeOnly EndTime);
public record ReplaceAvailabilityRequest([param: Required] List<AvailabilityRange> Items);
public record PtCoachDto(int Id, string FullName, string? AvatarUrl, string? Bio, string? Certifications, List<SportDto> Sports);
public record PtSlotDto(int CoachId, int SportId, DateOnly Date, TimeOnly StartTime, TimeOnly EndTime);
public record CreatePtBookingRequest(int CoachId, int SportId, DateOnly Date, TimeOnly StartTime, int? RoomId, int? MemberId);
public record ReschedulePtBookingRequest(DateOnly Date, TimeOnly StartTime, int? RoomId);
public record CancelPtBookingRequest([param: StringLength(500)] string? Reason);
public record PtBookingDto(int Id, int MemberId, int CoachId, int SportId, int? RoomId, int SubscriptionId,
    DateOnly Date, TimeOnly StartTime, TimeOnly EndTime, BookingStatus Status, bool CreditRefunded, int PerformedByUserId,
    bool IsLateCancellation = false);

public interface IPtService
{
    Task<List<AvailabilityItem>> GetAvailabilityAsync(int coachId, CancellationToken ct);
    Task<List<AvailabilityItem>> ReplaceAvailabilityAsync(int actorId, ReplaceAvailabilityRequest request, CancellationToken ct);
    Task<List<PtCoachDto>> ListCoachesAsync(int? sportId, CancellationToken ct);
    Task<List<PtSlotDto>> ListSlotsAsync(int coachId, int sportId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<PtBookingDto> BookAsync(int actorId, CreatePtBookingRequest request, CancellationToken ct);
    Task CancelAsync(int actorId, int bookingId, CancelPtBookingRequest request, CancellationToken ct);
    Task<PtBookingDto> RescheduleAsync(int actorId, int bookingId, ReschedulePtBookingRequest request, CancellationToken ct);
    Task CoachCancelAsync(int actorId, int bookingId, CancelPtBookingRequest request, CancellationToken ct);
}

public sealed class PtService(AppDbContext db, ISchedulingTransaction transaction, IAppClock clock,
    IOptions<SchedulingOptions> options) : IPtService
{
    private readonly SchedulingOptions settings = options.Value;

    public Task<List<AvailabilityItem>> GetAvailabilityAsync(int coachId, CancellationToken ct) => db.CoachAvailabilities.AsNoTracking()
        .Where(x => x.CoachId == coachId && x.IsActive).OrderBy(x => x.DayOfWeek).ThenBy(x => x.StartTime)
        .Select(x => new AvailabilityItem(x.Id, x.SportId, x.Sport.Name, x.DayOfWeek, x.StartTime, x.EndTime)).ToListAsync(ct);

    public async Task<List<AvailabilityItem>> ReplaceAvailabilityAsync(int actorId, ReplaceAvailabilityRequest request, CancellationToken ct)
    {
        ValidateAvailability(request.Items);
        return await transaction.ExecuteAsync(async token =>
        {
            await EnsureCoachAsync(actorId, null, token);
            var sportIds = request.Items.Select(x => x.SportId).Distinct().ToList();
            await transaction.LockAsync(sportIds.Select(x => new ScheduleResourceLock(ScheduleResourceType.Sport, x))
                .Append(new ScheduleResourceLock(ScheduleResourceType.Coach, actorId)), token);
            var coachSports = await db.CoachProfiles.Where(x => x.UserId == actorId).SelectMany(x => x.Sports).Where(x => sportIds.Contains(x.Id) && x.Status == SportStatus.Active && x.HasPt)
                .Select(x => x.Id).ToListAsync(token);
            if (coachSports.Count != sportIds.Count) throw SchedulingException.Conflict("COACH_NOT_ELIGIBLE", "Có bộ môn PT không thuộc chuyên môn của huấn luyện viên.");
            var today = DateOnly.FromDateTime(clock.LocalNow);
            var future = await db.PtBookings.Where(x => x.CoachId == actorId && x.Status == BookingStatus.Confirmed && x.Date >= today).ToListAsync(token);
            future = future.Where(x => clock.ToUtc(x.Date, x.EndTime) > clock.UtcNow).ToList();
            var invalid = future.Where(x => !request.Items.Any(a => a.SportId == x.SportId && a.DayOfWeek == x.Date.DayOfWeek && a.StartTime <= x.StartTime && a.EndTime >= x.EndTime)).ToList();
            if (invalid.Count != 0) throw SchedulingException.Conflict("AVAILABILITY_HAS_BOOKINGS", "Khung mới không bao phủ booking PT tương lai.", invalid.Select(x => x.Id));
            var current = await db.CoachAvailabilities.Where(x => x.CoachId == actorId && x.IsActive).ToListAsync(token);
            foreach (var item in current) item.IsActive = false;
            db.CoachAvailabilities.AddRange(request.Items.Select(x => new CoachAvailability
            {
                CoachId = actorId, SportId = x.SportId, DayOfWeek = x.DayOfWeek, StartTime = x.StartTime, EndTime = x.EndTime,
                CreatedAt = clock.UtcNow,
            }));
            SchedulingRules.AddAudit(db, actorId, "CoachAvailability.Replace", nameof(CoachAvailability), actorId, request);
            await db.SaveChangesAsync(token);
            return await GetAvailabilityAsync(actorId, token);
        }, ct);
    }

    public async Task<List<PtCoachDto>> ListCoachesAsync(int? sportId, CancellationToken ct)
    {
        var query = db.Users.AsNoTracking().Where(x => x.Role == UserRole.Coach && x.IsActive && x.CoachProfile != null &&
            x.CoachProfile.Sports.Any(s => s.Status == SportStatus.Active && s.HasPt));
        if (sportId is { } id) query = query.Where(x => x.CoachProfile!.Sports.Any(s => s.Id == id && s.Status == SportStatus.Active && s.HasPt));
        var coaches = await query.Include(x => x.CoachProfile!).ThenInclude(x => x.Sports).OrderBy(x => x.FullName).ToListAsync(ct);
        return coaches.Select(x => new PtCoachDto(x.Id, x.FullName, x.AvatarUrl, x.CoachProfile!.Bio, x.CoachProfile.Certifications,
            x.CoachProfile.Sports.Where(s => s.HasPt && s.Status == SportStatus.Active)
                .Select(s => new SportDto(s.Id, s.Name, s.Description, s.HasClasses, s.HasPt, s.Status)).ToList())).ToList();
    }

    public async Task<List<PtSlotDto>> ListSlotsAsync(int coachId, int sportId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (to < from || to.DayNumber - from.DayNumber > 31) throw new SchedulingException("INVALID_DATE_RANGE", "Khoảng tìm slot tối đa 31 ngày.");
        await EnsureCoachAsync(coachId, sportId, ct);
        var availability = await db.CoachAvailabilities.AsNoTracking().Where(x => x.CoachId == coachId && x.SportId == sportId && x.IsActive).ToListAsync(ct);
        var classBusy = await db.ClassSessions.AsNoTracking().Where(x => x.CoachId == coachId && x.Status != SessionStatus.Cancelled && x.Date >= from && x.Date <= to)
            .Select(x => new { x.Date, x.StartTime, x.EndTime }).ToListAsync(ct);
        var ptBusy = await db.PtBookings.AsNoTracking().Where(x => x.CoachId == coachId && x.Status == BookingStatus.Confirmed && x.Date >= from && x.Date <= to)
            .Select(x => new { x.Date, x.StartTime, x.EndTime }).ToListAsync(ct);
        var slots = new List<PtSlotDto>();
        for (var date = from; date <= to; date = date.AddDays(1))
        foreach (var range in availability.Where(x => x.DayOfWeek == date.DayOfWeek))
        for (var start = range.StartTime; start.AddHours(1) <= range.EndTime; start = start.AddHours(1))
        {
            var end = start.AddHours(1);
            if (clock.ToUtc(date, start) <= clock.UtcNow) continue;
            if (classBusy.Any(x => SchedulingRules.Overlaps(x.Date, x.StartTime, x.EndTime, date, start, end))) continue;
            if (ptBusy.Any(x => SchedulingRules.Overlaps(x.Date, x.StartTime, x.EndTime, date, start, end))) continue;
            slots.Add(new(coachId, sportId, date, start, end));
        }
        return slots;
    }

    public async Task<PtBookingDto> BookAsync(int actorId, CreatePtBookingRequest request, CancellationToken ct)
    {
        var memberId = await ResolveTargetMemberAsync(actorId, request.MemberId, ct);
        return await transaction.ExecuteAsync(async token =>
        {
            await LockBookingResourcesAsync(memberId, request.CoachId, request.SportId, request.RoomId, token);
            var subscriptionId = await ValidateNewBookingAsync(memberId, request.CoachId, request.SportId, request.Date, request.StartTime, request.RoomId, null, token);
            await ConsumeCreditAsync(subscriptionId, token);
            var booking = NewBooking(actorId, memberId, subscriptionId, request);
            db.PtBookings.Add(booking);
            await db.SaveChangesAsync(token);
            SchedulingRules.AddAudit(db, actorId, "PtBooking.Create", nameof(PtBooking), booking.Id,
                new { TargetMemberId = memberId, PerformedByUserId = actorId, booking.SubscriptionId, booking.CoachId, booking.Date, booking.StartTime });
            SchedulingRules.AddNotification(db, memberId, actorId, NotificationType.Booking, "Đặt lịch PT thành công",
                $"Lịch PT ngày {booking.Date:yyyy-MM-dd} lúc {booking.StartTime:HH:mm} đã được xác nhận.", $"pt-booking:{booking.Id}:created:{memberId}");
            SchedulingRules.AddNotification(db, booking.CoachId, actorId, NotificationType.Booking, "Có lịch PT mới",
                $"Lịch PT ngày {booking.Date:yyyy-MM-dd} lúc {booking.StartTime:HH:mm} đã được xác nhận.", $"pt-booking:{booking.Id}:created:coach:{booking.CoachId}");
            await db.SaveChangesAsync(token);
            return ToDto(booking);
        }, ct);
    }

    public async Task CancelAsync(int actorId, int bookingId, CancelPtBookingRequest request, CancellationToken ct)
    {
        var actor = await GetActiveActorAsync(actorId, ct);
        if (actor.Role is not (UserRole.Member or UserRole.Receptionist)) throw SchedulingException.Forbidden("ROLE_NOT_ALLOWED", "Vai trò không được hủy lịch PT.");
        await transaction.ExecuteAsync(async token =>
        {
            var booking = await LoadAndLockBookingAsync(bookingId, token);
            if (actor.Role == UserRole.Member && booking.MemberId != actorId) throw SchedulingException.Forbidden("BOOKING_NOT_OWNED", "Không thể hủy lịch của hội viên khác.");
            if (booking.Status == BookingStatus.Cancelled) return 0;
            EnsureFuture(booking);
            var refund = SchedulingRules.MeetsCancellationCutoff(clock.ToUtc(booking.Date, booking.StartTime), clock.UtcNow,
                settings.PtCancellationCutoffHours);
            if (!refund && !settings.AllowLatePtCancellation)
                throw SchedulingException.Conflict("PT_CANCELLATION_CUTOFF", $"Phải hủy PT trước giờ bắt đầu ít nhất {settings.PtCancellationCutoffHours} giờ.");
            CancelBooking(booking, actorId, request.Reason);
            booking.IsLateCancellation = !refund;
            if (refund) await RefundOnceAsync(booking, token);
            AddPtCancellationEffects(actorId, booking, "PtBooking.Cancel", refund);
            return 0;
        }, ct);
    }

    public async Task<PtBookingDto> RescheduleAsync(int actorId, int bookingId, ReschedulePtBookingRequest request, CancellationToken ct)
    {
        var actor = await GetActiveActorAsync(actorId, ct);
        if (actor.Role is not (UserRole.Member or UserRole.Receptionist)) throw SchedulingException.Forbidden("ROLE_NOT_ALLOWED", "Vai trò không được đổi lịch PT.");
        return await transaction.ExecuteAsync(async token =>
        {
            var old = await LoadAndLockBookingAsync(bookingId, token, request.RoomId);
            if (actor.Role == UserRole.Member && old.MemberId != actorId) throw SchedulingException.Forbidden("BOOKING_NOT_OWNED", "Không thể đổi lịch của hội viên khác.");
            if (old.Status != BookingStatus.Confirmed) throw SchedulingException.Conflict("BOOKING_NOT_ACTIVE", "Booking không còn hiệu lực.");
            EnsureFuture(old);
            if (old.Date == request.Date && old.StartTime == request.StartTime && old.RoomId == request.RoomId) return ToDto(old);
            var timely = SchedulingRules.MeetsCancellationCutoff(clock.ToUtc(old.Date, old.StartTime), clock.UtcNow,
                settings.PtCancellationCutoffHours);
            if (!timely && !settings.AllowLatePtCancellation)
                throw SchedulingException.Conflict("PT_CANCELLATION_CUTOFF", $"Phải đổi PT trước giờ bắt đầu ít nhất {settings.PtCancellationCutoffHours} giờ.");
            if (timely) await RefundOnceAsync(old, token);
            var subscriptionId = await ValidateNewBookingAsync(old.MemberId, old.CoachId, old.SportId, request.Date, request.StartTime, request.RoomId, old.Id, token);
            await ConsumeCreditAsync(subscriptionId, token);
            CancelBooking(old, actorId, "Rescheduled");
            old.IsLateCancellation = !timely;
            var created = NewBooking(actorId, old.MemberId, subscriptionId,
                new CreatePtBookingRequest(old.CoachId, old.SportId, request.Date, request.StartTime, request.RoomId, old.MemberId));
            db.PtBookings.Add(created);
            await db.SaveChangesAsync(token);
            SchedulingRules.AddAudit(db, actorId, "PtBooking.Reschedule", nameof(PtBooking), old.Id,
                new { NewBookingId = created.Id, TargetMemberId = old.MemberId, RefundedOldCredit = timely, Policy = timely ? "refund-old-consume-new" : "consume-additional-credit" });
            SchedulingRules.AddNotification(db, old.MemberId, actorId, NotificationType.ScheduleChange, "Đã đổi lịch PT",
                $"Lịch PT mới: {created.Date:yyyy-MM-dd} {created.StartTime:HH:mm}.", $"pt-booking:{old.Id}:rescheduled:{created.Id}");
            SchedulingRules.AddNotification(db, old.CoachId, actorId, NotificationType.ScheduleChange, "Lịch PT đã đổi",
                $"Lịch PT mới: {created.Date:yyyy-MM-dd} {created.StartTime:HH:mm}.", $"pt-booking:{old.Id}:rescheduled:{created.Id}:coach");
            await db.SaveChangesAsync(token);
            return ToDto(created);
        }, ct);
    }

    public async Task CoachCancelAsync(int actorId, int bookingId, CancelPtBookingRequest request, CancellationToken ct)
    {
        await transaction.ExecuteAsync(async token =>
        {
            await EnsureCoachAsync(actorId, null, token);
            var booking = await LoadAndLockBookingAsync(bookingId, token);
            if (booking.CoachId != actorId) throw SchedulingException.Forbidden("BOOKING_NOT_OWNED", "Huấn luyện viên chỉ được hủy lịch PT của mình.");
            if (booking.Status == BookingStatus.Cancelled) return 0;
            EnsureFuture(booking);
            CancelBooking(booking, actorId, request.Reason);
            await RefundOnceAsync(booking, token);
            AddPtCancellationEffects(actorId, booking, "PtBooking.CoachCancel", true);
            return 0;
        }, ct);
    }

    private async Task<int> ValidateNewBookingAsync(int memberId, int coachId, int sportId, DateOnly date, TimeOnly start,
        int? roomId, int? ignoredPtId, CancellationToken ct)
    {
        var end = start.AddHours(1);
        if (start.Minute != 0 || start.Second != 0) throw new SchedulingException("PT_SLOT_ALIGNMENT", "Slot PT phải bắt đầu đúng giờ và dài 60 phút.");
        SchedulingRules.EnsureBookingWindow(clock.ToUtc(date, start), clock.UtcNow,
            settings.PtBookingOpensDaysBefore, settings.PtBookingClosesMinutesBefore);
        await EnsureCoachAsync(coachId, sportId, ct);
        var memberActive = await db.Users.AnyAsync(x => x.Id == memberId && x.Role == UserRole.Member && x.IsActive, ct);
        if (!memberActive) throw SchedulingException.Conflict("MEMBER_INACTIVE", "Hội viên không hoạt động.");
        var available = await db.CoachAvailabilities.AnyAsync(x => x.CoachId == coachId && x.SportId == sportId && x.IsActive &&
            x.DayOfWeek == date.DayOfWeek && x.StartTime <= start && x.EndTime >= end, ct);
        if (!available) throw SchedulingException.Conflict("PT_SLOT_UNAVAILABLE", "Slot không thuộc availability của huấn luyện viên.");
        if (await HasMemberConflictAsync(memberId, date, start, end, ignoredPtId, ct)) throw SchedulingException.Conflict("MEMBER_SCHEDULE_CONFLICT", "Hội viên đã có lịch trùng giờ.");
        if (await HasCoachConflictAsync(coachId, date, start, end, ignoredPtId, ct)) throw SchedulingException.Conflict("COACH_SCHEDULE_CONFLICT", "Huấn luyện viên đã có lịch trùng giờ.");
        if (roomId is { } id)
        {
            var room = await db.Rooms.FindAsync([id], ct) ?? throw SchedulingException.NotFound("ROOM_NOT_FOUND", "Không tìm thấy phòng.");
            if (room.Status != RoomStatus.Active) throw SchedulingException.Conflict("ROOM_UNAVAILABLE", "Phòng không hoạt động.");
            var busy = await db.ClassSessions.AnyAsync(x => x.RoomId == id && x.Status != SessionStatus.Cancelled && x.Date == date && x.StartTime < end && start < x.EndTime, ct) ||
                await db.PtBookings.AnyAsync(x => x.RoomId == id && x.Status == BookingStatus.Confirmed && x.Id != ignoredPtId && x.Date == date && x.StartTime < end && start < x.EndTime, ct);
            if (busy) throw SchedulingException.Conflict("ROOM_SCHEDULE_CONFLICT", "Phòng đã có lịch trùng giờ.");
        }
        var subscriptionId = await db.MemberSubscriptions.AsNoTracking().Where(x => x.MemberId == memberId && x.Status == SubscriptionStatus.Active &&
                x.StartDate <= date && date <= x.EndDate && x.RemainingPtSessions > 0 && x.Plan.SportId == sportId && x.Plan.PtSessionCredits > 0 &&
                !db.Payments.Any(p => p.SubscriptionId == x.Id && p.Status == PaymentStatus.Pending))
            .OrderBy(x => x.EndDate).ThenBy(x => x.Id).Select(x => (int?)x.Id).FirstOrDefaultAsync(ct);
        return subscriptionId ?? throw SchedulingException.Conflict("PT_CREDIT_NOT_ELIGIBLE", "Không có gói PT Active đúng bộ môn, còn hạn và còn buổi.");
    }

    private async Task ConsumeCreditAsync(int subscriptionId, CancellationToken ct)
    {
        var changed = await db.MemberSubscriptions.Where(x => x.Id == subscriptionId && x.RemainingPtSessions > 0)
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.RemainingPtSessions, s => s.RemainingPtSessions - 1), ct);
        if (changed != 1) throw SchedulingException.Conflict("PT_CREDIT_EXHAUSTED", "Số buổi PT vừa được sử dụng bởi yêu cầu khác.");
    }

    private async Task RefundOnceAsync(PtBooking booking, CancellationToken ct)
    {
        if (booking.CreditRefunded) return;
        await db.MemberSubscriptions.Where(x => x.Id == booking.SubscriptionId)
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.RemainingPtSessions, s => s.RemainingPtSessions + 1), ct);
        booking.CreditRefunded = true; booking.CreditRefundedAt = clock.UtcNow;
    }

    private async Task<PtBooking> LoadAndLockBookingAsync(int bookingId, CancellationToken ct, int? additionalRoomId = null)
    {
        var ids = await db.PtBookings.AsNoTracking().Where(x => x.Id == bookingId).Select(x => new { x.SportId, x.MemberId, x.CoachId }).FirstOrDefaultAsync(ct)
            ?? throw SchedulingException.NotFound("PT_BOOKING_NOT_FOUND", "Không tìm thấy booking PT.");
        var locks = new List<ScheduleResourceLock>
        {
            new(ScheduleResourceType.Sport, ids.SportId), new(ScheduleResourceType.Member, ids.MemberId), new(ScheduleResourceType.Coach, ids.CoachId), new(ScheduleResourceType.PtBooking, bookingId),
        };
        if (additionalRoomId is { } roomId) locks.Add(new(ScheduleResourceType.Room, roomId));
        await transaction.LockAsync(locks, ct);
        return await db.PtBookings.FirstAsync(x => x.Id == bookingId, ct);
    }

    private Task LockBookingResourcesAsync(int memberId, int coachId, int sportId, int? roomId, CancellationToken ct)
    {
        var locks = new List<ScheduleResourceLock> { new(ScheduleResourceType.Sport, sportId), new(ScheduleResourceType.Member, memberId), new(ScheduleResourceType.Coach, coachId) };
        if (roomId is { } room) locks.Add(new(ScheduleResourceType.Room, room));
        return transaction.LockAsync(locks, ct);
    }

    private async Task<bool> HasMemberConflictAsync(int memberId, DateOnly date, TimeOnly start, TimeOnly end, int? ignoredPtId, CancellationToken ct) =>
        await db.ClassBookings.AnyAsync(x => x.MemberId == memberId && x.Status == BookingStatus.Confirmed && x.Session.Status != SessionStatus.Cancelled &&
            x.Session.Date == date && x.Session.StartTime < end && start < x.Session.EndTime, ct) ||
        await db.PtBookings.AnyAsync(x => x.MemberId == memberId && x.Status == BookingStatus.Confirmed && x.Id != ignoredPtId &&
            x.Date == date && x.StartTime < end && start < x.EndTime, ct);

    private async Task<bool> HasCoachConflictAsync(int coachId, DateOnly date, TimeOnly start, TimeOnly end, int? ignoredPtId, CancellationToken ct) =>
        await db.ClassSessions.AnyAsync(x => x.CoachId == coachId && x.Status != SessionStatus.Cancelled && x.Date == date &&
            x.StartTime < end && start < x.EndTime, ct) ||
        await db.PtBookings.AnyAsync(x => x.CoachId == coachId && x.Status == BookingStatus.Confirmed && x.Id != ignoredPtId &&
            x.Date == date && x.StartTime < end && start < x.EndTime, ct);

    private async Task EnsureCoachAsync(int coachId, int? sportId, CancellationToken ct)
    {
        var valid = await db.Users.AnyAsync(x => x.Id == coachId && x.Role == UserRole.Coach && x.IsActive &&
            (sportId == null || x.CoachProfile!.Sports.Any(s => s.Id == sportId && s.Status == SportStatus.Active && s.HasPt)), ct);
        if (!valid) throw SchedulingException.Conflict("COACH_NOT_ELIGIBLE", "Huấn luyện viên không hoạt động hoặc không đúng chuyên môn PT.");
    }

    private static void ValidateAvailability(List<AvailabilityRange> items)
    {
        if (items.Count > 50) throw new SchedulingException("AVAILABILITY_LIMIT", "Tối đa 50 khung availability.");
        foreach (var x in items)
            if (!Enum.IsDefined(x.DayOfWeek) || x.StartTime >= x.EndTime || x.StartTime.Minute != 0 || x.EndTime.Minute != 0 || (x.EndTime - x.StartTime).TotalMinutes % 60 != 0)
                throw new SchedulingException("INVALID_AVAILABILITY", "Availability phải thẳng giờ, kết thúc sau bắt đầu và chia hết thành slot 60 phút.");
        for (var i = 0; i < items.Count; i++)
        for (var j = i + 1; j < items.Count; j++)
            if (items[i].DayOfWeek == items[j].DayOfWeek && items[i].StartTime < items[j].EndTime && items[j].StartTime < items[i].EndTime)
                throw new SchedulingException("AVAILABILITY_OVERLAP", "Các khung availability không được trùng nhau.");
    }

    private async Task<int> ResolveTargetMemberAsync(int actorId, int? requested, CancellationToken ct)
    {
        var actor = await GetActiveActorAsync(actorId, ct);
        var target = actor.Role switch
        {
            UserRole.Member when requested is null || requested == actorId => actorId,
            UserRole.Member => throw SchedulingException.Forbidden("CANNOT_ACT_FOR_MEMBER", "Hội viên không thể thao tác hộ người khác."),
            UserRole.Receptionist when requested is not null => requested.Value,
            UserRole.Receptionist => throw new SchedulingException("TARGET_MEMBER_REQUIRED", "Lễ tân phải truyền memberId."),
            _ => throw SchedulingException.Forbidden("ROLE_NOT_ALLOWED", "Vai trò không được đặt PT."),
        };
        if (!await db.Users.AnyAsync(x => x.Id == target && x.Role == UserRole.Member && x.IsActive, ct))
            throw SchedulingException.NotFound("MEMBER_NOT_FOUND", "Không tìm thấy hội viên đang hoạt động.");
        return target;
    }

    private async Task<User> GetActiveActorAsync(int actorId, CancellationToken ct)
    {
        var actor = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == actorId, ct)
            ?? throw SchedulingException.Forbidden("ACTOR_NOT_FOUND", "Tài khoản không hợp lệ.");
        if (!actor.IsActive) throw SchedulingException.Forbidden("ACTOR_INACTIVE", "Tài khoản đã bị khóa.");
        return actor;
    }

    private void EnsureFuture(PtBooking booking)
    {
        if (booking.Status == BookingStatus.Completed || booking.AttendanceFinalizedAt != null ||
            clock.ToUtc(booking.Date, booking.StartTime) <= clock.UtcNow)
            throw SchedulingException.Conflict("PT_ALREADY_OCCURRED", "Không thể hủy booking đã diễn ra hoặc hoàn tất.");
    }

    private void CancelBooking(PtBooking booking, int actorId, string? reason)
    {
        booking.Status = BookingStatus.Cancelled; booking.CancelledAt = clock.UtcNow; booking.CancelledByUserId = actorId;
        booking.CancellationReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    }

    private void AddPtCancellationEffects(int actorId, PtBooking booking, string action, bool refunded)
    {
        SchedulingRules.AddAudit(db, actorId, action, nameof(PtBooking), booking.Id,
            new { TargetMemberId = booking.MemberId, booking.CancellationReason, CreditRefunded = refunded, booking.SubscriptionId, booking.IsLateCancellation });
        SchedulingRules.AddNotification(db, booking.MemberId, actorId, NotificationType.ScheduleChange, "Lịch PT đã hủy",
            $"Lịch PT ngày {booking.Date:yyyy-MM-dd} lúc {booking.StartTime:HH:mm} đã hủy. " + (refunded ? "Đã hoàn 1 buổi." : "Hủy muộn, không hoàn buổi."),
            $"pt-booking:{booking.Id}:cancelled");
        SchedulingRules.AddNotification(db, booking.CoachId, actorId, NotificationType.ScheduleChange, "Lịch PT đã hủy",
            $"Lịch PT ngày {booking.Date:yyyy-MM-dd} lúc {booking.StartTime:HH:mm} đã hủy.", $"pt-booking:{booking.Id}:cancelled:coach");
    }

    private PtBooking NewBooking(int actorId, int memberId, int subscriptionId, CreatePtBookingRequest x) => new()
    {
        MemberId = memberId, CoachId = x.CoachId, SportId = x.SportId, RoomId = x.RoomId, SubscriptionId = subscriptionId,
        PerformedByUserId = actorId, Date = x.Date, StartTime = x.StartTime, EndTime = x.StartTime.AddHours(1), CreatedAt = clock.UtcNow,
    };
    private static PtBookingDto ToDto(PtBooking x) => new(x.Id, x.MemberId, x.CoachId, x.SportId, x.RoomId, x.SubscriptionId,
        x.Date, x.StartTime, x.EndTime, x.Status, x.CreditRefunded, x.PerformedByUserId, x.IsLateCancellation);
}
