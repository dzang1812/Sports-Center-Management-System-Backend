using System.ComponentModel.DataAnnotations;
using SportsCenter.Services.Payments;
using SportsCenter.Services.Membership;
using Microsoft.Extensions.Options;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;

namespace SportsCenter.Services.Scheduling;

public record AvailabilityItem(int Id, int SportId, string Sport, DayOfWeek DayOfWeek, TimeOnly StartTime, TimeOnly EndTime);
public record AvailabilityRange(int SportId, DayOfWeek DayOfWeek, TimeOnly StartTime, TimeOnly EndTime);
public record ReplaceAvailabilityRequest([param: Required] List<AvailabilityRange> Items);
public record PtCoachDto(int Id, string FullName, string? AvatarUrl, string? Bio, string? Certifications, List<SportDto> Sports,
    double AverageRating = 0, int ReviewCount = 0, bool IsFeatured = false);
public record PtSlotDto(int CoachId, int SportId, DateOnly Date, TimeOnly StartTime, TimeOnly EndTime);
public record CreatePtBookingRequest(int CoachId, int SportId, DateOnly Date, TimeOnly StartTime, int? RoomId, int? MemberId, PtFundingSource FundingSource = PtFundingSource.LegacyCredit, decimal? ExpectedPrice = null);
public record ReschedulePtBookingRequest(DateOnly Date, TimeOnly StartTime, int? RoomId);
public record CancelPtBookingRequest([param: StringLength(500)] string? Reason);
public record ManagerCancelPtBookingRequest([param: Required, StringLength(500, MinimumLength = 2)] string Reason);
public record PtBookingListItem(int BookingId, int MemberId, string Member, int CoachId, string Coach,
    int SportId, string Sport, DateOnly Date, TimeOnly StartTime, TimeOnly EndTime,
    int? RoomId, string? Room, BookingStatus Status);
public record PtBookingDto(int Id, int MemberId, int CoachId, int SportId, int? RoomId, int? SubscriptionId,
    DateOnly Date, TimeOnly StartTime, TimeOnly EndTime, BookingStatus Status, bool CreditRefunded, int PerformedByUserId,
    bool IsLateCancellation = false, PtFundingSource FundingSource = PtFundingSource.LegacyCredit, int? FundingOrderId = null);

public interface IPtService
{
    Task<List<AvailabilityItem>> GetAvailabilityAsync(int coachId, CancellationToken ct);
    Task<List<AvailabilityItem>> ReplaceAvailabilityAsync(int actorId, ReplaceAvailabilityRequest request, CancellationToken ct);
    Task<WeekAvailabilityResponse> GetWeekAvailabilityAsync(int coachId, DateOnly weekStart, CancellationToken ct);
    Task<WeekAvailabilityResponse> ReplaceWeekAvailabilityAsync(int actorId, ReplaceWeekAvailabilityRequest request, CancellationToken ct);
    Task<List<PtCoachDto>> ListCoachesAsync(int? sportId, CancellationToken ct);
    Task<List<PtSlotDto>> ListSlotsAsync(int coachId, int sportId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<PtBookingDto> BookAsync(int actorId, CreatePtBookingRequest request, CancellationToken ct, string? idempotencyKey = null);
    Task<PtCounterCheckoutDto> CounterCheckoutAsync(int actorId, PtCounterCheckoutRequest request, string key, CancellationToken ct);
    Task<PtBookingDto> CompleteCounterCheckoutAsync(int actorId, int paymentId, CancellationToken ct);
    Task CancelAsync(int actorId, int bookingId, CancelPtBookingRequest request, CancellationToken ct);
    Task<PtBookingDto> RescheduleAsync(int actorId, int bookingId, ReschedulePtBookingRequest request, CancellationToken ct);
    Task CoachCancelAsync(int actorId, int bookingId, CancelPtBookingRequest request, CancellationToken ct);
    Task ManagerCancelAsync(int actorId, int bookingId, ManagerCancelPtBookingRequest request, CancellationToken ct);
    Task<PagedResponse<PtBookingListItem>> ListBookingsAsync(int actorId, DateOnly? from, DateOnly? to,
        int? memberId, int? coachId, int? sportId, BookingStatus? status, int page, int pageSize, CancellationToken ct);
}

public sealed partial class PtService(IPtRepository pt, IScheduleConflictRepository scheduleConflicts, ICatalogRepository catalog,
    IScheduleUserRepository users, ISchedulingSubscriptionRepository subscriptions, IAuditLogRepository audit, INotificationRepository notifications,
    ISchedulingTransaction transaction, IAppClock clock, IFlow2TimeRules timeRules, IOptions<SchedulingOptions> options, WalletService wallet, Flow2CommerceService commerce,
    ICommerceRepository commerceRepo, IMembershipRepository membershipRepo, ISubscriptionService payments, ICoachReviewRepository coachReviews) : IPtService
{
    private readonly SchedulingOptions settings = options.Value;

    public async Task<PagedResponse<PtBookingListItem>> ListBookingsAsync(int actorId, DateOnly? from, DateOnly? to,
        int? memberId, int? coachId, int? sportId, BookingStatus? status, int page, int pageSize, CancellationToken ct)
    {
        await users.EnsureActiveActorAsync(actorId, UserRole.Manager, ct);
        from ??= DateOnly.FromDateTime(clock.LocalNow);
        to ??= from.Value.AddDays(30);
        if (to < from || to.Value.DayNumber - from.Value.DayNumber > 180)
            throw new SchedulingException("INVALID_DATE_RANGE", "Khoảng lọc tối đa 180 ngày.");
        if (status is { } state && !Enum.IsDefined(state))
            throw new SchedulingException("INVALID_BOOKING_STATUS", "Trạng thái booking không hợp lệ.");
        page = Math.Max(1, page);
        pageSize = pageSize is 10 or 20 or 50 ? pageSize : 20;
        if (page > int.MaxValue / pageSize)
            throw new SchedulingException("INVALID_PAGE", "Trang vượt quá giới hạn cho phép.");
        var (rows, total) = await pt.ListBookingsAsync(from.Value, to.Value, memberId, coachId, sportId, status, page, pageSize, ct);
        return new(rows.Select(x => new PtBookingListItem(x.Id, x.MemberId, x.Member.FullName,
            x.CoachId, x.Coach.FullName, x.SportId, x.Sport.Name, x.Date, x.StartTime, x.EndTime,
            x.RoomId, x.Room?.Name, x.Status)).ToList(), total, page, pageSize);
    }

    // Compatibility reads show only this week. Recurring writers must upgrade.
    public async Task<List<AvailabilityItem>> GetAvailabilityAsync(int coachId, CancellationToken ct) =>
        (await GetWeekAvailabilityAsync(coachId, WeekStart(DateOnly.FromDateTime(clock.LocalNow)), ct)).Items
            .Select(x => new AvailabilityItem(x.Id, x.SportId, x.Sport, x.Date.DayOfWeek, x.StartTime, x.EndTime)).ToList();

    public Task<List<AvailabilityItem>> ReplaceAvailabilityAsync(int actorId, ReplaceAvailabilityRequest request, CancellationToken ct) =>
        throw new SchedulingException("AVAILABILITY_WEEK_REQUIRED", "Vui lòng tải lại giao diện và chọn tuần để lưu giờ rảnh.");

    public async Task<List<PtCoachDto>> ListCoachesAsync(int? sportId, CancellationToken ct)
    {
        var coaches = await users.ListPtCoachesAsync(sportId, ct);
        var ratings = await coachReviews.GetStatsAsync(ct);
        // Featured picks first, then best rated (more reviews break ties), then name.
        return coaches.Select(x =>
            {
                var r = ratings.GetValueOrDefault(x.Id);
                return new PtCoachDto(x.Id, x.FullName, x.AvatarUrl, x.CoachProfile!.Bio, x.CoachProfile.Certifications,
                    x.CoachProfile.Sports.Where(s => s.HasPt && s.Status == SportStatus.Active)
                        .Select(s => new SportDto(s.Id, s.Name, s.Description, s.HasClasses, s.HasPt, s.Status)).ToList(),
                    r is null ? 0 : Math.Round(r.Average, 1), r?.Count ?? 0, x.CoachProfile.IsFeatured);
            })
            .OrderByDescending(x => x.IsFeatured).ThenByDescending(x => x.AverageRating).ThenByDescending(x => x.ReviewCount)
            .ThenBy(x => x.FullName).ToList();
    }

    public async Task<List<PtSlotDto>> ListSlotsAsync(int coachId, int sportId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (from == DateOnly.MinValue || to == DateOnly.MaxValue || to < from || to.DayNumber - from.DayNumber > 31) throw new SchedulingException("INVALID_DATE_RANGE", "Khoảng tìm slot tối đa 31 ngày.");
        await EnsureCoachAsync(coachId, sportId, ct);
        var availability = await pt.GetActiveAvailabilityForSportAsync(coachId, sportId, from, to, ct);
        var classBusy = await pt.GetCoachClassBusyAsync(coachId, from, to, ct);
        var ptBusy = await pt.GetCoachPtBusyAsync(coachId, from, to, ct);
        var slots = new List<PtSlotDto>();
        for (var date = from; date <= to; date = date.AddDays(1))
        foreach (var range in availability.Where(x => x.Date == date))
        for (var start = range.StartTime; start < range.EndTime && start.AddHours(1) > start && start.AddHours(1) <= range.EndTime; start = start.AddHours(1))
        {
            var end = start.AddHours(1);
            if (timeRules.EnforceTimeRules && clock.ToUtc(date, start) <= clock.UtcNow) continue;
            if (classBusy.Any(x => SchedulingRules.Overlaps(x.Date, x.StartTime, x.EndTime, date, start, end))) continue;
            if (ptBusy.Any(x => SchedulingRules.Overlaps(x.Date, x.StartTime, x.EndTime, date, start, end))) continue;
            slots.Add(new(coachId, sportId, date, start, end));
        }
        return slots;
    }

    public async Task<PtBookingDto> BookAsync(int actorId, CreatePtBookingRequest request, CancellationToken ct, string? idempotencyKey = null)
    {
        if (!Enum.IsDefined(request.FundingSource)) throw new SchedulingException("INVALID_FUNDING_SOURCE", "Nguồn thanh toán không hợp lệ.");
        var memberId = await ResolveTargetMemberAsync(actorId, request.MemberId, ct);
        Task<PtBookingDto> Work(CancellationToken token) => BookCoreAsync(actorId, memberId, request, token);
        return request.FundingSource == PtFundingSource.LegacyCredit && string.IsNullOrWhiteSpace(idempotencyKey)
            ? await transaction.ExecuteAsync(Work, ct)
            : await wallet.ExecuteAsync(actorId, "pt-book", idempotencyKey ?? "", request, Work, ct);
    }

    // Called within the existing transaction/idempotency boundary, including counter cash + booking.
    private async Task<PtBookingDto> BookCoreAsync(int actorId, int memberId, CreatePtBookingRequest request, CancellationToken token)
    {
        await LockBookingResourcesAsync(memberId, request.CoachId, request.SportId, request.RoomId, token);
        int? subscriptionId = null;
        CommerceOrder? order = null;
        if (request.FundingSource == PtFundingSource.Wallet)
        {
            await ValidateBookingTargetAsync(memberId, request.CoachId, request.SportId, request.Date, request.StartTime, request.RoomId, null, token);
            if (clock.ToUtc(request.Date, request.StartTime) <= clock.UtcNow) throw SchedulingException.Conflict("BOOKING_CLOSED", "Không thanh toán buổi đã bắt đầu.");
            var sport = await catalog.GetSportAsync(request.SportId, token);
            if (sport?.PtSinglePrice is not { } price) throw SchedulingException.Conflict("PT_PRICE_UNSET", "Trung tâm chưa cấu hình giá PT lẻ.");
            Flow2CommerceService.EnsureExpectedPrice(request.ExpectedPrice, price);
            order = await commerce.PurchaseAsync(memberId, actorId, CommerceOrderKind.PtSingle, price,
                new { request.CoachId, request.SportId, Price = price, Minutes = 60, Policy = "pt-single-v1", CutoffHours = 12 }, token);
        }
        else
        {
            subscriptionId = await ValidateNewBookingAsync(memberId, request.CoachId, request.SportId, request.Date, request.StartTime, request.RoomId, null, token);
            await ConsumeCreditAsync(subscriptionId.Value, token);
        }
        var booking = NewBooking(actorId, memberId, subscriptionId, request);
        booking.FundingSource = request.FundingSource == PtFundingSource.Wallet ? PtFundingSource.Wallet
            : (await subscriptions.ListForMemberAndSportAsync(memberId, request.SportId, token)).Single(x => x.Id == subscriptionId).IsWalletPurchase
                ? PtFundingSource.PackageCredit : PtFundingSource.LegacyCredit;
        if (booking.FundingSource != PtFundingSource.LegacyCredit && clock.ToUtc(booking.Date, booking.StartTime) <= clock.UtcNow)
            throw SchedulingException.Conflict("BOOKING_CLOSED", "Không sử dụng quyền đã thanh toán cho buổi quá khứ.");
        booking.FundingOrderId = order?.Id;
        pt.AddBooking(booking); await transaction.SaveChangesAsync(token);
        audit.AddAudit(actorId, "PtBooking.Create", nameof(PtBooking), booking.Id,
            new { TargetMemberId = memberId, booking.SubscriptionId, booking.FundingSource, booking.FundingOrderId, booking.CoachId, booking.Date, booking.StartTime });
        notifications.AddNotification(memberId, actorId, NotificationType.Booking, "Đặt lịch PT thành công",
            $"Lịch PT ngày {booking.Date:yyyy-MM-dd} lúc {booking.StartTime:HH:mm} đã xác nhận.", $"pt-booking:{booking.Id}:created:{memberId}");
        notifications.AddNotification(booking.CoachId, actorId, NotificationType.Booking, "Có lịch PT mới",
            $"Lịch PT ngày {booking.Date:yyyy-MM-dd} lúc {booking.StartTime:HH:mm} đã xác nhận.", $"pt-booking:{booking.Id}:created:coach:{booking.CoachId}");
        await transaction.SaveChangesAsync(token);
        return ToDto(booking);
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
            EnsureCancellable(booking);
            if (await pt.HasAttendanceAsync(bookingId, token)) throw SchedulingException.Conflict("PT_ALREADY_OCCURRED", "Đã ghi nhận điểm danh.");
            var cutoffHours = booking.FundingSource == PtFundingSource.LegacyCredit ? settings.PtCancellationCutoffHours : 12;
            var refund = (booking.FundingSource == PtFundingSource.LegacyCredit && !timeRules.EnforceTimeRules) || SchedulingRules.MeetsCancellationCutoff(
                clock.ToUtc(booking.Date, booking.StartTime), clock.UtcNow, cutoffHours);
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
            EnsureCancellable(old);
            if (await pt.HasAttendanceAsync(bookingId, token)) throw SchedulingException.Conflict("PT_ALREADY_OCCURRED", "Đã ghi nhận điểm danh.");
            if (old.Date == request.Date && old.StartTime == request.StartTime && old.RoomId == request.RoomId) return ToDto(old);
            var timely = (old.FundingSource == PtFundingSource.LegacyCredit && !timeRules.EnforceTimeRules) || SchedulingRules.MeetsCancellationCutoff(
                clock.ToUtc(old.Date, old.StartTime), clock.UtcNow, settings.PtCancellationCutoffHours);
            if (!timely && !settings.AllowLatePtCancellation)
                throw SchedulingException.Conflict("PT_CANCELLATION_CUTOFF", $"Phải đổi PT trước giờ bắt đầu ít nhất {settings.PtCancellationCutoffHours} giờ.");
            await ValidateBookingTargetAsync(old.MemberId, old.CoachId, old.SportId, request.Date, request.StartTime, request.RoomId, old.Id, token);
            await ValidateRescheduleSubscriptionAsync(old, request.Date, token);
            if (old.FundingSource != PtFundingSource.LegacyCredit && clock.ToUtc(request.Date, request.StartTime) <= clock.UtcNow)
                throw SchedulingException.Conflict("BOOKING_CLOSED", "Không chuyển quyền đã thanh toán sang buổi quá khứ.");
            CancelBooking(old, actorId, "Rescheduled");
            old.FundingTransferred = true;
            old.IsLateCancellation = !timely;
            await transaction.SaveChangesAsync(token); // release the active funding allocation; rollback restores it if creation fails
            var created = NewBooking(actorId, old.MemberId, old.SubscriptionId,
                new CreatePtBookingRequest(old.CoachId, old.SportId, request.Date, request.StartTime, request.RoomId, old.MemberId));
            created.FundingSource = old.FundingSource; created.FundingOrderId = old.FundingOrderId;
            pt.AddBooking(created);
            await transaction.SaveChangesAsync(token);
            audit.AddAudit(actorId, "PtBooking.Reschedule", nameof(PtBooking), old.Id,
                new { NewBookingId = created.Id, TargetMemberId = old.MemberId, ReusedSubscriptionId = old.SubscriptionId, Policy = "reuse-consumed-credit" });
            notifications.AddNotification(old.MemberId, actorId, NotificationType.ScheduleChange, "Đã đổi lịch PT",
                $"Lịch PT mới: {created.Date:yyyy-MM-dd} {created.StartTime:HH:mm}.", $"pt-booking:{old.Id}:rescheduled:{created.Id}");
            notifications.AddNotification(old.CoachId, actorId, NotificationType.ScheduleChange, "Lịch PT đã đổi",
                $"Lịch PT mới: {created.Date:yyyy-MM-dd} {created.StartTime:HH:mm}.", $"pt-booking:{old.Id}:rescheduled:{created.Id}:coach");
            await transaction.SaveChangesAsync(token);
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
            EnsureCancellable(booking);
            if (await pt.HasAttendanceAsync(bookingId, token)) throw SchedulingException.Conflict("PT_ALREADY_OCCURRED", "Đã ghi nhận điểm danh.");
            CancelBooking(booking, actorId, request.Reason);
            await RefundOnceAsync(booking, token);
            AddPtCancellationEffects(actorId, booking, "PtBooking.CoachCancel", true);
            return 0;
        }, ct);
    }

    public async Task ManagerCancelAsync(int actorId, int bookingId, ManagerCancelPtBookingRequest request, CancellationToken ct)
    {
        await transaction.ExecuteAsync(async token =>
        {
            await users.EnsureActiveActorAsync(actorId, UserRole.Manager, token);
            var booking = await LoadAndLockBookingAsync(bookingId, token);
            // Cancelled bookings of any origin stay unchanged: never refund a transferred/forfeited credit.
            if (booking.Status == BookingStatus.Cancelled) return 0;
            // Central cancellation always protects started/attended data, including in local test mode.
            if (booking.Status != BookingStatus.Confirmed || booking.AttendanceFinalizedAt != null ||
                clock.ToUtc(booking.Date, booking.StartTime) <= clock.UtcNow || await pt.HasAttendanceAsync(bookingId, token))
                throw SchedulingException.Conflict("PT_ALREADY_OCCURRED", "Không thể hủy booking đã bắt đầu hoặc ghi nhận điểm danh.");
            CancelBooking(booking, actorId, request.Reason);
            booking.IsLateCancellation = false;
            await RefundOnceAsync(booking, token);
            AddPtCancellationEffects(actorId, booking, "PtBooking.ManagerCancel", true);
            return 0;
        }, ct);
    }

    private async Task<int> ValidateNewBookingAsync(int memberId, int coachId, int sportId, DateOnly date, TimeOnly start,
        int? roomId, int? ignoredPtId, CancellationToken ct)
    {
        await ValidateBookingTargetAsync(memberId, coachId, sportId, date, start, roomId, ignoredPtId, ct);
        var subscriptionId = (await subscriptions.ListForMemberAndSportAsync(memberId, sportId, ct))
            .Where(x => SchedulingRules.SubscriptionCovers(x, sportId, date,
                needsPt: true, enforceTimeRules: timeRules.EnforceTimeRules))
            .OrderBy(x => x.EndDate).ThenBy(x => x.Id).Select(x => (int?)x.Id).FirstOrDefault();
        return subscriptionId ?? throw SchedulingException.Conflict("PT_CREDIT_NOT_ELIGIBLE", "Không có gói PT Active đúng bộ môn, còn hạn và còn buổi.");
    }

    private async Task ValidateBookingTargetAsync(int memberId, int coachId, int sportId, DateOnly date, TimeOnly start,
        int? roomId, int? ignoredPtId, CancellationToken ct)
    {
        var end = start.AddHours(1);
        if (end <= start || start.Minute != 0 || start.Second != 0) throw new SchedulingException("PT_SLOT_ALIGNMENT", "Slot PT phải bắt đầu đúng giờ và dài 60 phút.");
        var startUtc = clock.ToUtc(date, start);
        if (timeRules.EnforceTimeRules)
            SchedulingRules.EnsureBookingWindow(startUtc, clock.UtcNow,
                settings.PtBookingOpensDaysBefore, settings.PtBookingClosesMinutesBefore);
        await EnsureCoachAsync(coachId, sportId, ct);
        var memberActive = await users.IsActiveInRoleAsync(memberId, UserRole.Member, ct);
        if (!memberActive) throw SchedulingException.Conflict("MEMBER_INACTIVE", "Hội viên không hoạt động.");
        var available = await pt.IsAvailableAsync(coachId, sportId, date, start, end, ct);
        if (!available) throw SchedulingException.Conflict("PT_SLOT_UNAVAILABLE", "Slot không thuộc availability của huấn luyện viên.");
        if (await scheduleConflicts.HasMemberConflictAsync(memberId, date, start, end, ignoredPtId, ct)) throw SchedulingException.Conflict("MEMBER_SCHEDULE_CONFLICT", "Hội viên đã có lịch trùng giờ.");
        if (await scheduleConflicts.HasCoachConflictAsync(coachId, date, start, end, ignoredPtId, ct)) throw SchedulingException.Conflict("COACH_SCHEDULE_CONFLICT", "Huấn luyện viên đã có lịch trùng giờ.");
        if (roomId is { } id)
        {
            var room = await catalog.GetRoomAsync(id, ct) ?? throw SchedulingException.NotFound("ROOM_NOT_FOUND", "Không tìm thấy phòng.");
            if (room.Status != RoomStatus.Active) throw SchedulingException.Conflict("ROOM_UNAVAILABLE", "Phòng không hoạt động.");
            if (await scheduleConflicts.IsRoomBusyAsync(id, date, start, end, ignoredPtId, ct))
                throw SchedulingException.Conflict("ROOM_SCHEDULE_CONFLICT", "Phòng đã có lịch trùng giờ.");
        }
    }

    private async Task ValidateRescheduleSubscriptionAsync(PtBooking booking, DateOnly targetDate, CancellationToken ct)
    {
        if (booking.FundingSource == PtFundingSource.Wallet) return;
        var subscription = (await subscriptions.ListForMemberAndSportAsync(booking.MemberId, booking.SportId, ct))
            .SingleOrDefault(x => x.Id == booking.SubscriptionId);
        var coversTarget = subscription is not null &&
            (subscription.Status == SubscriptionStatus.Active ||
                (!timeRules.EnforceTimeRules && !subscription.IsWalletPurchase && subscription.Status == SubscriptionStatus.Expired)) &&
            (!timeRules.EnforceTimeRules && !subscription.IsWalletPurchase || subscription.StartDate <= targetDate && targetDate <= subscription.EndDate) &&
            (subscription.SportId is null || subscription.SportId == booking.SportId) &&
            !subscription.HasPendingPayment && subscription.PtSessionCredits > 0;
        if (!coversTarget)
            throw SchedulingException.Conflict("PT_SUBSCRIPTION_NOT_ELIGIBLE_FOR_RESCHEDULE",
                "Gói PT đã dùng cho booking không còn hợp lệ tại ngày tập mới.");
    }

    private async Task ConsumeCreditAsync(int subscriptionId, CancellationToken ct)
    {
        var changed = await subscriptions.TryConsumePtCreditAsync(subscriptionId, ct);
        if (changed != 1) throw SchedulingException.Conflict("PT_CREDIT_EXHAUSTED", "Số buổi PT vừa được sử dụng bởi yêu cầu khác.");
    }

    private async Task RefundOnceAsync(PtBooking booking, CancellationToken ct)
    {
        if (booking.FundingTransferred) throw SchedulingException.Conflict("PT_FUNDING_TRANSFERRED", "Nguồn quyền lợi đã chuyển booking mới.");
        if (booking.FundingSource == PtFundingSource.Wallet) { await commerce.RefundPtAsync(booking.CancelledByUserId!.Value, booking, ct); return; }
        if (booking.CreditRefunded) return;
        var changed = await subscriptions.TryRefundPtCreditAsync(booking.SubscriptionId!.Value, ct);
        if (changed != 1)
            throw SchedulingException.Conflict("PT_CREDIT_REFUND_CONFLICT",
                "Không thể hoàn buổi PT vì số buổi của gói vừa thay đổi.");
        booking.CreditRefunded = true; booking.CreditRefundedAt = clock.UtcNow;
    }

    private async Task<PtBooking> LoadAndLockBookingAsync(int bookingId, CancellationToken ct, int? additionalRoomId = null)
    {
        var ids = await pt.GetBookingKeyAsync(bookingId, ct)
            ?? throw SchedulingException.NotFound("PT_BOOKING_NOT_FOUND", "Không tìm thấy booking PT.");
        var locks = new List<ScheduleResourceLock>
        {
            new(ScheduleResourceType.Sport, ids.SportId), new(ScheduleResourceType.Member, ids.MemberId), new(ScheduleResourceType.Coach, ids.CoachId), new(ScheduleResourceType.PtBooking, bookingId),
        };
        if (additionalRoomId is { } roomId) locks.Add(new(ScheduleResourceType.Room, roomId));
        await transaction.LockAsync(locks, ct);
        return await pt.GetBookingAsync(bookingId, ct);
    }

    private Task LockBookingResourcesAsync(int memberId, int coachId, int sportId, int? roomId, CancellationToken ct)
    {
        var locks = new List<ScheduleResourceLock> { new(ScheduleResourceType.Sport, sportId), new(ScheduleResourceType.Member, memberId), new(ScheduleResourceType.Coach, coachId) };
        if (roomId is { } room) locks.Add(new(ScheduleResourceType.Room, room));
        return transaction.LockAsync(locks, ct);
    }

    private async Task EnsureCoachAsync(int coachId, int? sportId, CancellationToken ct)
    {
        if (!await users.IsPtCoachAsync(coachId, sportId, ct))
            throw SchedulingException.Conflict("COACH_NOT_ELIGIBLE", "Huấn luyện viên không hoạt động hoặc không đúng chuyên môn PT.");
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
        if (!await users.IsActiveInRoleAsync(target, UserRole.Member, ct))
            throw SchedulingException.NotFound("MEMBER_NOT_FOUND", "Không tìm thấy hội viên đang hoạt động.");
        return target;
    }

    private async Task<User> GetActiveActorAsync(int actorId, CancellationToken ct)
    {
        var actor = await users.GetUserAsync(actorId, ct)
            ?? throw SchedulingException.Forbidden("ACTOR_NOT_FOUND", "Tài khoản không hợp lệ.");
        if (!actor.IsActive) throw SchedulingException.Forbidden("ACTOR_INACTIVE", "Tài khoản đã bị khóa.");
        return actor;
    }

    private void EnsureCancellable(PtBooking booking)
    {
        if (booking.Status == BookingStatus.Completed || booking.AttendanceFinalizedAt != null ||
            ((timeRules.EnforceTimeRules || booking.FundingSource != PtFundingSource.LegacyCredit) && clock.ToUtc(booking.Date, booking.StartTime) <= clock.UtcNow))
            throw SchedulingException.Conflict("PT_ALREADY_OCCURRED", "Không thể hủy booking đã diễn ra hoặc hoàn tất.");
    }

    private void CancelBooking(PtBooking booking, int actorId, string? reason)
    {
        booking.Status = BookingStatus.Cancelled; booking.CancelledAt = clock.UtcNow; booking.CancelledByUserId = actorId;
        booking.CancellationReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    }

    private void AddPtCancellationEffects(int actorId, PtBooking booking, string action, bool refunded)
    {
        audit.AddAudit(actorId, action, nameof(PtBooking), booking.Id,
            new { TargetMemberId = booking.MemberId, booking.CancellationReason, booking.CreditRefunded, MoneyRefunded = refunded && booking.FundingSource == PtFundingSource.Wallet, booking.FundingSource, booking.FundingOrderId, booking.SubscriptionId, booking.IsLateCancellation });
        notifications.AddNotification(booking.MemberId, actorId, NotificationType.ScheduleChange, "Lịch PT đã hủy",
            $"Lịch PT ngày {booking.Date:yyyy-MM-dd} lúc {booking.StartTime:HH:mm} đã hủy. " + (refunded ? (booking.FundingSource == PtFundingSource.Wallet ? "Đã hoàn tiền vào số dư." : "Đã hoàn 1 buổi.") : "Hủy muộn, không hoàn quyền lợi."),
            $"pt-booking:{booking.Id}:cancelled");
        notifications.AddNotification(booking.CoachId, actorId, NotificationType.ScheduleChange, "Lịch PT đã hủy",
            $"Lịch PT ngày {booking.Date:yyyy-MM-dd} lúc {booking.StartTime:HH:mm} đã hủy.", $"pt-booking:{booking.Id}:cancelled:coach");
    }

    private PtBooking NewBooking(int actorId, int memberId, int? subscriptionId, CreatePtBookingRequest x) => new()
    {
        MemberId = memberId, CoachId = x.CoachId, SportId = x.SportId, RoomId = x.RoomId, SubscriptionId = subscriptionId,
        PerformedByUserId = actorId, Date = x.Date, StartTime = x.StartTime, EndTime = x.StartTime.AddHours(1), CreatedAt = clock.UtcNow,
    };
    private static PtBookingDto ToDto(PtBooking x) => new(x.Id, x.MemberId, x.CoachId, x.SportId, x.RoomId, x.SubscriptionId,
        x.Date, x.StartTime, x.EndTime, x.Status, x.CreditRefunded, x.PerformedByUserId, x.IsLateCancellation, x.FundingSource, x.FundingOrderId);
}
