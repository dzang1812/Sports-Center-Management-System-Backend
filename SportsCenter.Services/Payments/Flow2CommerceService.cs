using System.Text.Json;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.Services.Payments;

public record BuyRequest(int? MemberId, decimal? ExpectedPrice = null);
public record CoursePublishRequest(bool Open);
public record CourseTermsRequest(decimal Fee, int ExpectedSessions, DateTime EnrollmentClosesAt);
public record CourseCutoffRequest(DateTime EnrollmentClosesAt);
public record CourseCancelRequest(string Reason);
public record CourseSessionCancellation(string Reason, bool RefundWithoutMakeup, DateOnly? Date, TimeOnly? StartTime, TimeOnly? EndTime, int? RoomId);
public record EnrollmentResult(int EnrollmentId, int OrderId, decimal PaidAmount, int SessionCount);
public record PackageResult(int SubscriptionId, int OrderId, decimal PaidAmount);
public record PtPriceRequest(decimal Price);

public static class CommercePolicy
{
    // These are financial/entitlement boundaries, deliberately independent of local time-rule bypass.
    public static void RequireBefore(DateTime now, DateTime cutoff)
    {
        if (now >= cutoff) throw SchedulingException.Conflict("COURSE_ENROLLMENT_CLOSED", "Đã hết hạn đăng ký khóa; không nhận học viên giữa khóa.");
    }
    public static decimal[] Allocate(decimal paid, int count)
    {
        WalletService.ValidateMoney(paid);
        if (count <= 0) throw new SchedulingException("COURSE_SCHEDULE_INCOMPLETE", "Cần lịch buổi học đầy đủ.");
        var unit = decimal.Floor(paid / count);
        var remainder = (int)(paid - unit * count);
        return Enumerable.Range(0, count).Select(i => unit + (i < remainder ? 1m : 0m)).ToArray();
    }
}

public sealed partial class Flow2CommerceService(ICommerceRepository repo, WalletService wallet, ISchedulingTransaction tx,
    IScheduleUserRepository users, IScheduleConflictRepository conflicts, IAuditLogRepository audit,
    INotificationRepository notifications, IAppClock clock)
{

    public async Task<object> PtPricesAsync(CancellationToken ct) => await repo.ListPtPricesAsync(ct);

    public Task<int> SetPtPriceAsync(int actor, int sportId, PtPriceRequest request, string key, CancellationToken ct) =>
        wallet.ExecuteAsync(actor, $"pt-price:{sportId}", key, request, async token =>
        {
            await users.EnsureActiveActorAsync(actor, UserRole.Manager, token);
            WalletService.ValidateMoney(request.Price);
            await tx.LockAsync([new(ScheduleResourceType.Sport, sportId)], token);
            var sport = await repo.FindPtSportAsync(sportId, token)
                ?? throw SchedulingException.NotFound("SPORT_NOT_FOUND", "Không tìm thấy bộ môn PT.");
            sport.PtSinglePrice = request.Price;
            audit.AddAudit(actor, "PtPrice.Update", nameof(Sport), sportId, request);
            return sportId;
        }, ct);

    public async Task<object> CoursesAsync(int actor, CancellationToken ct)
    {
        var user = await ActiveAsync(actor, ct);
        var items = await repo.ListCoursesAsync(user.Role == UserRole.Manager, ct);
        return items.Select(x => new
        {
            course = x,
            canEnroll = x.SportStatus == SportStatus.Active && x.SportHasClasses && x.Status == ClassStatus.Open && x.PublishedAt != null && x.EnrollmentClosesAt > clock.UtcNow && x.CourseStartsAt > clock.UtcNow && x.Enrolled < x.Capacity,
            enrollmentUnavailableReason = x.SportStatus != SportStatus.Active ? "Bộ môn đang ngừng hoạt động."
                : !x.SportHasClasses ? "Bộ môn chưa bật lớp/khóa học. Vui lòng liên hệ quản lý." : null,
        });
    }

    public async Task<object> CourseAsync(int actor, int id, CancellationToken ct)
    {
        var user = await ActiveAsync(actor, ct);
        var c = await CourseEntityAsync(id, ct);
        if (user.Role != UserRole.Manager && (c.PublishedAt == null || c.Status == ClassStatus.Draft)) throw SchedulingException.NotFound("COURSE_NOT_FOUND", "Khóa chưa công bố.");
        var sessions = await repo.ListCourseScheduleAsync(id, ct);
        return new { c.Id, c.Name, c.Description, c.Level, c.CourseFee, c.ExpectedSessions, c.EnrollmentClosesAt, c.PublishedAt, c.Status, c.Capacity, sessions,
            durationWeeks = (int)Math.Ceiling((c.EndDate.DayNumber - c.StartDate.DayNumber + 1) / 7m),
            sessionPrice = c.ExpectedSessions > 0 && c.CourseFee % c.ExpectedSessions == 0 ? (decimal?)(c.CourseFee / c.ExpectedSessions) : null,
            validationIssues = CourseSetupIssues(c, sessions.Count(s => s.Status != SessionStatus.Cancelled)),
            policy = "Hủy toàn khóa trước khai giảng: hoàn số tiền thực trả chưa hoàn. Sau khai giảng: liên hệ hỗ trợ. Nghỉ một buổi không hoàn tiền. Trung tâm hủy: bù buổi hoặc hoàn phân bổ snapshot." };
    }

    public async Task<object> EnrollmentsAsync(int actor, int? memberId, int? courseId, CancellationToken ct)
    {
        var user = await ActiveAsync(actor, ct);
        var target = user.Role == UserRole.Manager ? memberId : await wallet.TargetAsync(actor, memberId, false, ct);
        return await repo.ListPaidEnrollmentsAsync(target, courseId, ct);
    }

    public Task<int> TermsAsync(int actor, int id, CourseTermsRequest request, string key, CancellationToken ct) =>
        wallet.ExecuteAsync(actor, $"course-terms:{id}", key, request, async token =>
        {
            await users.EnsureActiveActorAsync(actor, UserRole.Manager, token);
            var c = await LockCourseAsync(id, [], token);
            if (await repo.CourseHasSalesAsync(id, token))
                throw SchedulingException.Conflict("COURSE_TERMS_SOLD", "Không sửa giá/số buổi của khóa đã bán. Tạo khóa mới.");
            if (c.EnrollmentClosesAt is { } old) CommercePolicy.RequireBefore(clock.UtcNow, old);
            WalletService.ValidateMoney(request.Fee);
            if (request.ExpectedSessions is < 1 or > 366 || request.EnrollmentClosesAt.Kind != DateTimeKind.Utc)
                throw new SchedulingException("INVALID_COURSE_TERMS", "Số buổi 1..366 và cutoff UTC có Z là bắt buộc.");
            var first = await repo.FindFirstActiveCourseSessionAsync(id, token);
            if (first != null && request.EnrollmentClosesAt > clock.ToUtc(first.Date, first.StartTime))
                throw new SchedulingException("INVALID_ENROLLMENT_CUTOFF", "Hạn đăng ký không được muộn hơn khai giảng.");
            if (request.EnrollmentClosesAt <= clock.UtcNow) throw new SchedulingException("INVALID_ENROLLMENT_CUTOFF", "Hạn đăng ký phải ở tương lai.");
            audit.AddAudit(actor, "Course.Terms", nameof(Class), id, new { Old = c.EnrollmentClosesAt, request });
            c.CourseFee = request.Fee; c.ExpectedSessions = request.ExpectedSessions; c.EnrollmentClosesAt = request.EnrollmentClosesAt;
            // Changes require an explicit publish validation again.
            c.Status = ClassStatus.Draft; c.PublishedAt = null; c.CourseStartsAt = null;
            return id;
        }, ct);

    public Task<int> PublishAsync(int actor, int id, CoursePublishRequest request, string key, CancellationToken ct) =>
        wallet.ExecuteAsync(actor, $"course-publish:{id}", key, request, async token =>
        {
            await users.EnsureActiveActorAsync(actor, UserRole.Manager, token);
            var c = await LockCourseAsync(id, [], token);
            if (c.Status == ClassStatus.Cancelled) throw SchedulingException.Conflict("COURSE_CANCELLED", "Khóa đã hủy.");
            if (request.Open)
            {
                var publishedSessions = await ValidateCourseAsync(c, token);
                c.CourseStartsAt ??= clock.ToUtc(publishedSessions[0].Date, publishedSessions[0].StartTime);
                CommercePolicy.RequireBefore(clock.UtcNow, c.CourseStartsAt.Value);
                CommercePolicy.RequireBefore(clock.UtcNow, c.EnrollmentClosesAt!.Value);
                c.PublishedAt ??= clock.UtcNow;
            }
            c.Status = request.Open ? ClassStatus.Open : ClassStatus.Closed;
            audit.AddAudit(actor, "Course.Publish", nameof(Class), id, request);
            return id;
        }, ct);

    public Task<int> CutoffAsync(int actor, int id, CourseCutoffRequest request, string key, CancellationToken ct) =>
        wallet.ExecuteAsync(actor, $"course-cutoff:{id}", key, request, async token =>
        {
            await users.EnsureActiveActorAsync(actor, UserRole.Manager, token);
            var c = await LockCourseAsync(id, [], token);
            if (c.EnrollmentClosesAt is null || c.Status == ClassStatus.Cancelled) throw SchedulingException.Conflict("COURSE_NOT_EDITABLE", "Khóa chưa có điều khoản hoặc đã hủy.");
            CommercePolicy.RequireBefore(clock.UtcNow, c.EnrollmentClosesAt.Value);
            var first = await repo.FindFirstActiveCourseSessionAsync(id, token);
            if (request.EnrollmentClosesAt.Kind != DateTimeKind.Utc || request.EnrollmentClosesAt <= clock.UtcNow || first == null || request.EnrollmentClosesAt > clock.ToUtc(first.Date, first.StartTime))
                throw new SchedulingException("INVALID_ENROLLMENT_CUTOFF", "Hạn đăng ký UTC phải ở tương lai và không muộn hơn buổi đầu.");
            audit.AddAudit(actor, "Course.Cutoff", nameof(Class), id, new { Previous = c.EnrollmentClosesAt, Current = request.EnrollmentClosesAt });
            c.EnrollmentClosesAt = request.EnrollmentClosesAt;
            return id;
        }, ct);

    public Task<EnrollmentResult> EnrollAsync(int actor, int id, BuyRequest request, string key, CancellationToken ct) =>
        wallet.ExecuteAsync(actor, $"course-buy:{id}", key, request, async token =>
        {
            var member = await wallet.TargetAsync(actor, request.MemberId, true, token);
            var c = await LockCourseAsync(id, [member], token);
            if (c.Status != ClassStatus.Open || c.PublishedAt == null) throw SchedulingException.Conflict("COURSE_NOT_OPEN", "Khóa chưa mở đăng ký.");
            if (c.EnrollmentClosesAt == null) throw SchedulingException.Conflict("COURSE_SCHEDULE_INCOMPLETE", "Chưa có hạn đăng ký.");
            CommercePolicy.RequireBefore(clock.UtcNow, c.EnrollmentClosesAt.Value);
            var sessions = await ValidateCourseAsync(c, token);
            CommercePolicy.RequireBefore(clock.UtcNow, c.CourseStartsAt ?? clock.ToUtc(sessions[0].Date, sessions[0].StartTime));
            var old = await repo.FindMemberEnrollmentAsync(id, member, token);
            if (old?.Status == EnrollmentStatus.Confirmed) throw SchedulingException.Conflict("ENROLLMENT_EXISTS", "Đã có đăng ký khóa hiệu lực.");
            var count = await repo.CountConfirmedPaidEnrollmentsAsync(id, token);
            if (count >= c.Capacity) throw SchedulingException.Conflict("COURSE_FULL", "Khóa đã hết chỗ.");
            foreach (var s in sessions)
            {
                if (await repo.CountConfirmedSessionBookingsAsync(s.Id, token) >= s.Capacity)
                    throw SchedulingException.Conflict("SESSION_FULL", "Một buổi của khóa đã hết chỗ.");
                if (await conflicts.HasMemberConflictAsync(member, s.Date, s.StartTime, s.EndTime, null, token))
                    throw SchedulingException.Conflict("MEMBER_SCHEDULE_CONFLICT", "Hội viên trùng lịch trong khóa.");
            }
            // Boundary is checked after waiting for locks and all validation, immediately before debit.
            CommercePolicy.RequireBefore(clock.UtcNow, c.EnrollmentClosesAt.Value);
            EnsureExpectedPrice(request.ExpectedPrice, c.CourseFee);
            var order = await PurchaseAsync(member, actor, CommerceOrderKind.Course, c.CourseFee,
                new { c.Id, c.Name, c.SportId, c.ExpectedSessions, c.CourseFee,
                    SessionPrice = c.ExpectedSessions > 0 && c.CourseFee % c.ExpectedSessions == 0 ? (decimal?)(c.CourseFee / c.ExpectedSessions) : null,
                    c.StartDate, c.EndDate, c.EnrollmentClosesAt, Policy = "course-v1", Sessions = sessions.Select(x => new { x.Id, x.Date, x.StartTime, x.EndTime }).ToArray() }, token);
            var enrollment = old ?? new ClassEnrollment { ClassId = id, MemberId = member };
            if (old == null) repo.Add(enrollment);
            enrollment.OrderId = order.Id; enrollment.Status = EnrollmentStatus.Confirmed; enrollment.EnrolledAt = clock.UtcNow; enrollment.CancelledAt = null;
            await tx.SaveChangesAsync(token);
            var allocation = CommercePolicy.Allocate(order.PaidAmount, sessions.Count);
            for (var i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                repo.Add(new CourseEntitlement { EnrollmentId = enrollment.Id, OrderId = order.Id, OriginalSessionId = s.Id, CurrentSessionId = s.Id, Value = allocation[i] });
                repo.Add(new ClassBooking { SessionId = s.Id, MemberId = member, EnrollmentId = enrollment.Id, PerformedByUserId = actor, CreatedAt = clock.UtcNow });
            }
            audit.AddAudit(actor, "Course.Enroll", nameof(ClassEnrollment), enrollment.Id, new { member, order.Id, Count = sessions.Count });
            notifications.AddNotification(member, actor, NotificationType.Booking, "Đăng ký khóa thành công", $"Đã có lịch toàn bộ {sessions.Count} buổi của {c.Name}.", $"course-order:{order.Id}:enrolled");
            if (c.CoachId is { } coach) notifications.AddNotification(coach, actor, NotificationType.Booking, "Có học viên đăng ký khóa", c.Name, $"course-order:{order.Id}:coach");
            CommercePolicy.RequireBefore(clock.UtcNow, c.EnrollmentClosesAt.Value);
            return new EnrollmentResult(enrollment.Id, order.Id, order.PaidAmount, sessions.Count);
        }, ct);

    public Task<int> CancelEnrollmentAsync(int actor, int enrollmentId, string reason, string key, CancellationToken ct) =>
        wallet.ExecuteAsync(actor, $"course-cancel-enrollment:{enrollmentId}", key, new { reason }, async token =>
        {
            var snapshot = await repo.FindPaidEnrollmentAsync(enrollmentId, token)
                ?? throw SchedulingException.NotFound("ENROLLMENT_NOT_FOUND", "Không tìm thấy enrollment trả ví.");
            RequireReason(reason);
            await wallet.TargetAsync(actor, snapshot.MemberId, true, token);
            var c = await LockCourseAsync(snapshot.ClassId, [snapshot.MemberId], token);
            var enrollment = await repo.GetEnrollmentAsync(enrollmentId, token);
            if (enrollment.Status == EnrollmentStatus.Cancelled) return enrollmentId;
            // The original cutoff never moves later once elapsed; conservative cancellation boundary.
            CommercePolicy.RequireBefore(clock.UtcNow, c.CourseStartsAt!.Value);
            if (await repo.EnrollmentHasAttendanceAsync(enrollmentId, token))
                throw SchedulingException.Conflict("ENROLLMENT_ATTENDANCE_RECORDED", "Enrollment đã ghi nhận tham dự; cần hỗ trợ.");
            var units = await repo.ListUnrefundedOrderUnitsAsync(enrollment.OrderId, token);
            foreach (var unit in units) await RefundUnitAsync(actor, unit, token);
            await CancelEnrollmentBookingsAsync(actor, enrollment, reason, token);
            return enrollmentId;
        }, ct);

    public Task<int> RequestSupportAsync(int actor, int enrollmentId, string reason, string key, CancellationToken ct) =>
        wallet.ExecuteAsync(actor, $"course-support:{enrollmentId}", key, new { reason }, async token =>
        {
            RequireReason(reason);
            var enrollment = await repo.FindPaidEnrollmentAsync(enrollmentId, token)
                ?? throw SchedulingException.NotFound("ENROLLMENT_NOT_FOUND", "Không tìm thấy enrollment trả phí.");
            await wallet.TargetAsync(actor, enrollment.MemberId, true, token);
            var request = new SupportRequest { MemberId = enrollment.MemberId, Subject = $"Course:{enrollment.ClassId}:Enrollment:{enrollmentId}", Description = reason.Trim(), CreatedAt = clock.UtcNow };
            repo.Add(request); await tx.SaveChangesAsync(token);
            audit.AddAudit(actor, "Course.SupportRequested", nameof(SupportRequest), request.Id, new { enrollmentId, enrollment.OrderId, enrollment.MemberId });
            return request.Id;
        }, ct);

    public async Task<object> SupportRequestsAsync(int actor, int courseId, CancellationToken ct)
    {
        await users.EnsureActiveActorAsync(actor, UserRole.Manager, ct);
        var prefix = $"Course:{courseId}:Enrollment:";
        return await repo.ListSupportRequestsAsync(prefix, ct);
    }

    public Task<int> CancelCourseAsync(int actor, int id, CourseCancelRequest request, string key, CancellationToken ct) =>
        wallet.ExecuteAsync(actor, $"course-cancel:{id}", key, request, async token =>
        {
            await users.EnsureActiveActorAsync(actor, UserRole.Manager, token);
            RequireReason(request.Reason);
            var c = await LockCourseAsync(id, [], token);
            if (c.Status == ClassStatus.Cancelled) return id;
            var sessionAttendance = await repo.ListCourseAttendanceAsync(id, token);
            if (sessionAttendance.Any(x => clock.ToUtc(x.Session!.Date, x.Session.StartTime) > clock.UtcNow))
                throw SchedulingException.Conflict("COURSE_FUTURE_ATTENDANCE", "Có buổi tương lai đã check-in trong test; không hủy quyền lợi/tài chính tự động.");
            var enrollments = await repo.ListConfirmedPaidEnrollmentsAsync(id, token);
            var units = await repo.ListCurrentCourseUnitsAsync(id, token);
            foreach (var u in units)
                if (u.CurrentSession.Status == SessionStatus.Cancelled || clock.ToUtc(u.CurrentSession.Date, u.CurrentSession.StartTime) > clock.UtcNow)
                    await RefundUnitAsync(actor, u, token);
            foreach (var enrollment in enrollments) await CancelEnrollmentBookingsAsync(actor, enrollment, request.Reason, token);
            var sessions = await repo.ListScheduledCourseSessionsAsync(id, token);
            foreach (var s in sessions.Where(x => clock.ToUtc(x.Date, x.StartTime) > clock.UtcNow))
            { s.Status = SessionStatus.Cancelled; s.CancelledAt = clock.UtcNow; s.CancelledByUserId = actor; s.CancellationReason = request.Reason; }
            c.Status = ClassStatus.Cancelled;
            audit.AddAudit(actor, "Course.Cancel", nameof(Class), id, request);
            if (c.CoachId is { } coach) notifications.AddNotification(coach, actor, NotificationType.ScheduleChange, "Khóa đã hủy", c.Name, $"course:{id}:cancelled:coach");
            return id;
        }, ct);

    public Task<int> CancelSessionAsync(int actor, int id, int sessionId, CourseSessionCancellation request, string key, CancellationToken ct) =>
        wallet.ExecuteAsync(actor, $"course-session-cancel:{sessionId}", key, request, async token =>
        {
            await users.EnsureActiveActorAsync(actor, UserRole.Manager, token);
            RequireReason(request.Reason);
            var c = await LockCourseAsync(id, [], token);
            if (c.Status == ClassStatus.Cancelled) throw SchedulingException.Conflict("COURSE_CANCELLED", "Khóa đã hủy.");
            var s = await repo.FindCourseSessionAsync(id, sessionId, token)
                ?? throw SchedulingException.NotFound("SESSION_NOT_FOUND", "Không tìm thấy buổi học.");
            if (s.Status != SessionStatus.Scheduled || s.AttendanceFinalizedAt != null || clock.ToUtc(s.Date, s.StartTime) <= clock.UtcNow ||
                await repo.SessionHasAttendanceAsync(sessionId, token))
                throw SchedulingException.Conflict("SESSION_NOT_CANCELLABLE", "Chỉ hủy buổi tương lai chưa điểm danh; buổi đã xử lý không xử lý lần hai.");
            var bookings = await repo.ListConfirmedSessionBookingsAsync(sessionId, token);
            var units = await repo.ListCurrentSessionUnitsAsync(sessionId, token);
            if (request.RefundWithoutMakeup) { foreach (var u in units) await RefundUnitAsync(actor, u, token); }
            else
            {
                if (request.Date is not { } date || request.StartTime is not { } start || request.EndTime is not { } end || request.RoomId is not { } roomId || start >= end || clock.ToUtc(date, start) <= clock.UtcNow)
                    throw new SchedulingException("MAKEUP_REQUIRED", "Cần lịch buổi bù tương lai hợp lệ.");
                if (end - start != s.EndTime - s.StartTime) throw SchedulingException.Conflict("COURSE_DURATION_IMMUTABLE", "Buổi bù phải giữ thời lượng quyền lợi đã bán.");
                var room = await repo.FindRoomAsync(roomId, token);
                if (room == null || room.Status != RoomStatus.Active || room.Capacity < s.Capacity) throw SchedulingException.Conflict("ROOM_UNAVAILABLE", "Phòng không đủ điều kiện.");
                if (s.CoachId is not { } coach || !await users.IsCoachForSportAsync(coach, c.SportId, token)) throw SchedulingException.Conflict("COACH_NOT_ELIGIBLE", "Coach không hợp lệ.");
                if ((await conflicts.FindResourceConflictsAsync([date], start, end, roomId, coach, s.Id, token)).Count != 0 ||
                    (await conflicts.FindMemberConflictsAsync(bookings.Select(x => x.MemberId).ToList(), date, start, end, s.Id, null, token)).Count != 0)
                    throw SchedulingException.Conflict("MAKEUP_CONFLICT", "Buổi bù gây trùng lịch.");
                var makeup = new ClassSession { ClassId = id, ReplacesSessionId = sessionId, Date = date, StartTime = start, EndTime = end, RoomId = roomId,
                    CoachId = coach, Capacity = s.Capacity, ClassName = s.ClassName, Level = s.Level, CreatedAt = clock.UtcNow };
                repo.Add(makeup); await tx.SaveChangesAsync(token);
                foreach (var u in units) u.CurrentSessionId = makeup.Id;
                foreach (var b in bookings) repo.Add(new ClassBooking { SessionId = makeup.Id, MemberId = b.MemberId, EnrollmentId = b.EnrollmentId, PerformedByUserId = actor, CreatedAt = clock.UtcNow });
                if (date > c.EndDate) c.EndDate = date; // makeup extends calendar, not the sold unit count or enrollment cutoff
                var makeupStart = clock.ToUtc(date, start);
                if (c.CourseStartsAt > makeupStart) c.CourseStartsAt = makeupStart;
                if (c.EnrollmentClosesAt > makeupStart)
                {
                    audit.AddAudit(actor, "Course.CutoffShortenedForMakeup", nameof(Class), id, new { Previous = c.EnrollmentClosesAt, Current = makeupStart });
                    c.EnrollmentClosesAt = makeupStart;
                }
            }
            s.Status = SessionStatus.Cancelled; s.CancelledAt = clock.UtcNow; s.CancelledByUserId = actor; s.CancellationReason = request.Reason;
            foreach (var b in bookings) { b.Status = BookingStatus.Cancelled; b.CancelledAt = clock.UtcNow; b.CancelledByUserId = actor; b.CancellationReason = request.Reason;
                notifications.AddNotification(b.MemberId, actor, NotificationType.ScheduleChange, "Buổi khóa đã hủy", request.RefundWithoutMakeup ? "Đã hoàn phân bổ buổi học vào số dư." : "Đã tạo buổi bù trong lịch.", $"course-session:{s.Id}:cancel:{b.MemberId}"); }
            if (s.CoachId is { } coachId) notifications.AddNotification(coachId, actor, NotificationType.ScheduleChange, "Thay đổi buổi khóa", request.Reason, $"course-session:{s.Id}:cancel:coach");
            audit.AddAudit(actor, "Course.SessionCancel", nameof(ClassSession), s.Id, request);
            // Once any sold unit is cancelled, stop sales until a separately designed new edition. Never reopen cutoff implicitly.
            c.Status = ClassStatus.Closed;
            return s.Id;
        }, ct);

    public Task<PackageResult> BuyPackageAsync(int actor, int planId, BuyRequest request, string key, CancellationToken ct) =>
        wallet.ExecuteAsync(actor, $"pt-package-buy:{planId}", key, request, async token =>
        {
            var member = await wallet.TargetAsync(actor, request.MemberId, true, token);
            await tx.LockAsync([new(ScheduleResourceType.Member, member), new(ScheduleResourceType.MembershipPlan, planId)], token);
            var p = await repo.FindPlanAsync(planId, token);
            if (p is null || !p.IsActive || p.PtSessionCredits <= 0 || p.IncludesClasses || p.IncludesGymAccess)
                throw SchedulingException.Conflict("PT_PACKAGE_REQUIRED", "Chỉ gói PT thuần đang hoạt động được mua bằng số dư; Gym/legacy giữ luồng hiện tại.");
            var start = DateOnly.FromDateTime(clock.LocalNow);
            var end = p.DurationMonths > 0 ? start.AddMonths(p.DurationMonths).AddDays(-1) : start.AddDays(p.DurationDays - 1);
            EnsureExpectedPrice(request.ExpectedPrice, p.Price);
            var order = await PurchaseAsync(member, actor, CommerceOrderKind.PtPackage, p.Price,
                new { p.Id, p.Name, p.Price, p.PtSessionCredits, p.SportId, StartDate = start, EndDate = end, Policy = "pt-package-v1", CutoffHours = 12 }, token);
            var sub = new MemberSubscription { MemberId = member, PlanId = p.Id, PurchaseOrderId = order.Id, SnapshotPtCredits = p.PtSessionCredits,
                SnapshotSportId = p.SportId, StartDate = start, EndDate = end, Status = SubscriptionStatus.Active, RemainingPtSessions = p.PtSessionCredits };
            repo.Add(sub); await tx.SaveChangesAsync(token);
            audit.AddAudit(actor, "PtPackage.Buy", nameof(MemberSubscription), sub.Id, new { member, order.Id });
            return new PackageResult(sub.Id, order.Id, order.PaidAmount);
        }, ct);

    public Task<int> RefundPackageAsync(int actor, int subId, string key, CancellationToken ct) =>
        wallet.ExecuteAsync(actor, $"pt-package-refund:{subId}", key, new { subId }, async token =>
        {
            var snap = await repo.FindPurchasedSubscriptionAsync(subId, token)
                ?? throw SchedulingException.NotFound("PACKAGE_NOT_FOUND", "Không phải gói mua ví.");
            await wallet.TargetAsync(actor, snap.MemberId, true, token);
            await tx.LockAsync([new(ScheduleResourceType.Member, snap.MemberId)], token);
            var sub = await repo.GetSubscriptionAsync(subId, token);
            var order = await repo.GetOrderAsync(sub.PurchaseOrderId, token);
            if (order.Kind != CommerceOrderKind.PtPackage)
                throw SchedulingException.Conflict("PT_PACKAGE_REQUIRED", "Chỉ hoàn gói PT tại chức năng này. Gói tự tập vui lòng liên hệ hỗ trợ.");
            if (order.RefundedAmount == order.PaidAmount) return subId;
            if (sub.EverUsedPtCredit || sub.RemainingPtSessions != sub.SnapshotPtCredits || await repo.SubscriptionHasPtBookingsAsync(subId, token))
                throw SchedulingException.Conflict("PACKAGE_REFUND_SUPPORT_REQUIRED", "Gói đã sử dụng/giữ credit; liên hệ hỗ trợ, không tự hoàn một phần.");
            await RefundOrderAsync(actor, order, order.PaidAmount, $"order:{order.Id}:package-refund", token);
            sub.Status = SubscriptionStatus.Cancelled; sub.RemainingPtSessions = 0;
            audit.AddAudit(actor, "PtPackage.Refund", nameof(MemberSubscription), subId, new { order.Id });
            return subId;
        }, ct);

    public async Task<CommerceOrder> PurchaseAsync(int member, int actor, CommerceOrderKind kind, decimal price, object snapshot, CancellationToken ct)
    {
        WalletService.ValidateMoney(price);
        var order = new CommerceOrder { MemberId = member, ActorId = actor, Kind = kind, PaidAmount = price, Snapshot = JsonSerializer.Serialize(snapshot), CreatedAt = clock.UtcNow };
        repo.Add(order); await tx.SaveChangesAsync(ct);
        await wallet.MoveAsync(member, actor, -price, "Purchase", $"order:{order.Id}:purchase", order.Id, null, ct);
        return order;
    }

    public async Task RefundOrderAsync(int actor, CommerceOrder order, decimal amount, string reference, CancellationToken ct)
    {
        if (amount == 0) return;
        if (amount < 0 || order.RefundedAmount + amount > order.PaidAmount) throw SchedulingException.Conflict("REFUND_LIMIT", "Khoản hoàn vượt số thực trả chưa hoàn.");
        order.RefundedAmount += amount;
        await wallet.MoveAsync(order.MemberId, actor, amount, "Refund", reference, order.Id, null, ct);
    }

    public async Task RefundPtAsync(int actor, PtBooking booking, CancellationToken ct)
    {
        if (booking.FundingSource != PtFundingSource.Wallet || booking.FundingTransferred) throw SchedulingException.Conflict("PT_FUNDING_TRANSFERRED", "Nguồn thanh toán đã chuyển hoặc không phải tiền ví.");
        var order = await repo.GetOrderAsync(booking.FundingOrderId, ct);
        if (order.RefundedAmount == order.PaidAmount) return;
        await RefundOrderAsync(actor, order, order.PaidAmount - order.RefundedAmount, $"order:{order.Id}:pt-refund", ct);
    }

    private async Task RefundUnitAsync(int actor, CourseEntitlement unit, CancellationToken ct)
    {
        if (unit.Refunded) return;
        var order = await repo.GetOrderAsync(unit.OrderId, ct);
        await RefundOrderAsync(actor, order, unit.Value, $"course-unit:{unit.Id}:refund", ct);
        unit.Refunded = true;
    }

    private async Task CancelEnrollmentBookingsAsync(int actor, ClassEnrollment e, string reason, CancellationToken ct)
    {
        var bookings = await repo.ListConfirmedEnrollmentBookingsAsync(e.Id, ct);
        foreach (var b in bookings.Where(x => clock.ToUtc(x.Session.Date, x.Session.StartTime) > clock.UtcNow))
        { b.Status = BookingStatus.Cancelled; b.CancelledAt = clock.UtcNow; b.CancelledByUserId = actor; b.CancellationReason = reason; }
        e.Status = EnrollmentStatus.Cancelled; e.CancelledAt = clock.UtcNow;
        audit.AddAudit(actor, "Course.CancelEnrollment", nameof(ClassEnrollment), e.Id, new { e.MemberId, e.OrderId, reason });
        notifications.AddNotification(e.MemberId, actor, NotificationType.ScheduleChange, "Đăng ký khóa đã hủy", reason, $"course-order:{e.OrderId}:cancel");
    }

    private async Task<Class> CourseEntityAsync(int id, CancellationToken ct) =>
        await repo.FindCourseAsync(id, tracking: true, ct)
        ?? throw SchedulingException.NotFound("COURSE_NOT_FOUND", "Không tìm thấy khóa.");

    private async Task<Class> LockCourseAsync(int id, List<int> extraMembers, CancellationToken ct)
    {
        var snap = await repo.FindCourseAsync(id, tracking: false, ct)
            ?? throw SchedulingException.NotFound("COURSE_NOT_FOUND", "Không tìm thấy khóa.");
        // Sport first serializes every schedule writer for this course, including resource changes.
        await tx.LockAsync([new(ScheduleResourceType.Sport, snap.SportId)], ct);
        var sessions = await repo.ListCourseSessionsForLockAsync(id, ct);
        var members = await repo.ListConfirmedCourseMembersAsync(id, ct);
        var locks = members.Concat(extraMembers).Select(x => new ScheduleResourceLock(ScheduleResourceType.Member, x)).ToList();
        locks.AddRange(sessions.Where(x => x.CoachId != null).Select(x => new ScheduleResourceLock(ScheduleResourceType.Coach, x.CoachId!.Value)));
        locks.AddRange(sessions.Select(x => new ScheduleResourceLock(ScheduleResourceType.Room, x.RoomId)));
        locks.Add(new(ScheduleResourceType.Class, id)); locks.AddRange(sessions.Select(x => new ScheduleResourceLock(ScheduleResourceType.Session, x.Id)));
        await tx.LockAsync(locks, ct);
        return await CourseEntityAsync(id, ct);
    }

    private async Task<List<ClassSession>> ValidateCourseAsync(Class c, CancellationToken ct)
    {
        var sessions = await repo.ListActiveCourseSessionsWithRoomsAsync(c.Id, ct);
        var issue = CourseSetupIssues(c, sessions.Count).FirstOrDefault();
        if (issue != null) throw new SchedulingException(issue.ErrorCode, issue.Message, 409, issue);
        WalletService.ValidateMoney(c.CourseFee);
        if (c.EnrollmentClosesAt > clock.ToUtc(sessions[0].Date, sessions[0].StartTime)) throw new SchedulingException("COURSE_CUTOFF_AFTER_START", "Hạn đăng ký không được sau buổi học đầu tiên.", 409, new { field = "enrollmentClosesAt" });
        var sport = await repo.GetSportAsync(c.SportId, ct);
        if (sport.Status != SportStatus.Active)
            throw SchedulingException.Conflict("SPORT_UNAVAILABLE", $"Bộ môn {sport.Name} đang ngừng hoạt động. Vui lòng liên hệ quản lý.");
        if (!sport.HasClasses)
            throw SchedulingException.Conflict("SPORT_UNAVAILABLE", $"Bộ môn {sport.Name} chưa bật lớp/khóa học. Quản lý cần bật quyền lớp học cho bộ môn này.");
        foreach (var s in sessions)
        {
            if (s.CoachId is not { } coach || !await users.IsCoachForSportAsync(coach, c.SportId, ct) || s.Room.Status != RoomStatus.Active || s.Capacity > s.Room.Capacity || s.Capacity < c.Capacity)
                throw SchedulingException.Conflict("COURSE_RESOURCE_INVALID", "Coach/phòng/sức chứa buổi không hợp lệ.");
            if ((await conflicts.FindResourceConflictsAsync([s.Date], s.StartTime, s.EndTime, s.RoomId, coach, s.Id, ct)).Count != 0)
                throw SchedulingException.Conflict("COURSE_SCHEDULE_CONFLICT", "Lịch khóa có xung đột.");
        }
        return sessions;
    }

    private async Task<User> ActiveAsync(int actor, CancellationToken ct)
    {
        var user = await users.GetUserAsync(actor, ct);
        return user is { IsActive: true } ? user : throw SchedulingException.Forbidden("ACTOR_INACTIVE", "Tài khoản không hoạt động.");
    }
    public static void EnsureExpectedPrice(decimal? expected, decimal actual)
    {
        if (expected is not null && expected != actual) throw SchedulingException.Conflict("PRICE_CHANGED", "Giá đã thay đổi. Tải lại giá và xác nhận giao dịch mới.");
    }
    private static void RequireReason(string reason)
    { if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length is < 2 or > 500) throw new SchedulingException("REASON_REQUIRED", "Lý do 2..500 ký tự."); }
}
