using System.ComponentModel.DataAnnotations;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Membership;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.Services.Operations;

public record SupportRequestDto(int Id, int MemberId, string MemberName, string MemberEmail, SupportCategory Category, string Subject,
    string Description, SupportStatus Status, string? HandledByName, string? CreatedByName, DateTime CreatedAt, DateTime LastActivityAt,
    DateTime? ResolvedAt);

public record SupportMessageDto(int Id, int AuthorId, string AuthorName, UserRole AuthorRole, string Body, bool IsInternal, DateTime CreatedAt);

public record SupportDetailDto(SupportRequestDto Request, List<SupportMessageDto> Messages);

public record SupportSummaryDto(int Open, int InProgress, int WaitingForMember);

public record CreateSupportRequest(
    SupportCategory Category,
    [param: Required(ErrorMessage = "Tiêu đề là bắt buộc"), StringLength(150, MinimumLength = 2, ErrorMessage = "Tiêu đề 2–150 ký tự")] string Subject,
    [param: Required(ErrorMessage = "Nội dung là bắt buộc"), StringLength(2000, MinimumLength = 2, ErrorMessage = "Nội dung 2–2000 ký tự")] string Description,
    int? MemberId = null);

public record ReplySupportRequest(
    [param: Required(ErrorMessage = "Nội dung là bắt buộc"), StringLength(2000, MinimumLength = 1, ErrorMessage = "Nội dung tối đa 2000 ký tự")] string Body,
    bool IsInternal = false);

public record UpdateSupportStatusRequest(SupportStatus Status);

public record ReportTrendPoint(string Period, decimal Revenue, int NewMembers, int NewClassBookings);

public record ClassFillDto(int ClassId, string Name, string? Coach, ClassStatus Status, ClassBookingMode BookingMode, int Capacity,
    int ActiveEnrollments, int Sessions, int ActiveBookings, decimal? FillRate);

public record CoachMessageRequest(
    int SessionId,
    bool WholeClass,
    NotificationType Type,
    [param: Required(ErrorMessage = "Tiêu đề là bắt buộc"), StringLength(150, MinimumLength = 2, ErrorMessage = "Tiêu đề 2–150 ký tự")] string Title,
    [param: Required(ErrorMessage = "Nội dung là bắt buộc"), StringLength(2000, MinimumLength = 2, ErrorMessage = "Nội dung 2–2000 ký tự")] string Body);

public record CoachMessageResult(int Recipients);

internal static class Staff
{
    public static async Task<User> EnsureAsync(IUserRepository users, int actorId, params UserRole[] roles)
    {
        var actor = await users.GetByIdAsync(actorId);
        if (actor is null || !roles.Contains(actor.Role))
            throw SchedulingException.Forbidden("ACTOR_NOT_ALLOWED", "Tài khoản không hợp lệ hoặc không có quyền.");
        return actor;
    }

    public static (int Page, int PageSize) Paging(int page, int pageSize) => (Math.Max(1, page), pageSize is 10 or 20 or 50 ? pageSize : 10);
}

public sealed class SupportService(IFrontDeskRepository repo, IUserRepository users, INotificationRepository notifications,
    IAuditLogRepository audit, IAppClock clock)
{
    // Member submits their own; front-desk staff log one on behalf of a member (MemberId required) and take it on.
    public async Task<SupportRequestDto> CreateAsync(int actorId, CreateSupportRequest r, CancellationToken ct)
    {
        var actor = await Staff.EnsureAsync(users, actorId, UserRole.Member, UserRole.Receptionist, UserRole.Manager);
        var staff = actor.Role != UserRole.Member;
        var memberId = staff ? r.MemberId ?? throw new SchedulingException("MEMBER_REQUIRED", "Cần chọn hội viên.") : actorId;
        if (staff && (await users.GetByIdAsync(memberId))?.Role != UserRole.Member)
            throw SchedulingException.NotFound("MEMBER_NOT_FOUND", "Không tìm thấy hội viên.");

        var now = clock.UtcNow;
        var request = new SupportRequest
        {
            MemberId = memberId, Category = r.Category, Subject = r.Subject.Trim(), Description = r.Description.Trim(),
            Status = staff ? SupportStatus.InProgress : SupportStatus.Open, CreatedById = staff ? actorId : null,
            HandledById = staff ? actorId : null, CreatedAt = now, LastActivityAt = now,
        };
        repo.AddSupportRequest(request);
        if (staff) Notify(request, actorId, $"Trung tâm đã ghi nhận yêu cầu của bạn: {request.Subject}.");
        await repo.SaveChangesAsync(ct);
        return ToDto((await repo.GetSupportRequestAsync(request.Id, ct))!);
    }

    public async Task<PagedResponse<SupportRequestDto>> ListMineAsync(int memberId, int page, int pageSize, CancellationToken ct)
    {
        await MembershipRules.EnsureActiveRoleAsync(users, memberId, UserRole.Member);
        (page, pageSize) = Staff.Paging(page, pageSize);
        var (items, total) = await repo.ListSupportRequestsAsync(memberId, null, null, null, page, pageSize, ct);
        return new(items.Select(ToDto).ToList(), total, page, pageSize);
    }

    public async Task<PagedResponse<SupportRequestDto>> ListAsync(int actorId, SupportStatus? status, SupportCategory? category, string? q,
        int page, int pageSize, CancellationToken ct)
    {
        await Staff.EnsureAsync(users, actorId, UserRole.Receptionist, UserRole.Manager);
        (page, pageSize) = Staff.Paging(page, pageSize);
        var (items, total) = await repo.ListSupportRequestsAsync(null, status, category, q, page, pageSize, ct);
        return new(items.Select(ToDto).ToList(), total, page, pageSize);
    }

    public async Task<SupportSummaryDto> SummaryAsync(int actorId, CancellationToken ct)
    {
        await Staff.EnsureAsync(users, actorId, UserRole.Receptionist, UserRole.Manager);
        var counts = await repo.CountSupportByStatusAsync(ct);
        return new(counts.GetValueOrDefault(SupportStatus.Open), counts.GetValueOrDefault(SupportStatus.InProgress),
            counts.GetValueOrDefault(SupportStatus.WaitingForMember));
    }

    public async Task<SupportDetailDto> GetAsync(int actorId, int id, CancellationToken ct)
    {
        var (actor, request) = await LoadAsync(actorId, id, ct);
        var staff = actor.Role != UserRole.Member;
        var messages = await repo.ListSupportMessagesAsync(id, staff, ct);
        return new(ToDto(request), messages.Select(m => new SupportMessageDto(m.Id, m.AuthorId, m.Author.FullName, m.Author.Role, m.Body,
            m.IsInternal, m.CreatedAt)).ToList());
    }

    public async Task<SupportDetailDto> ReplyAsync(int actorId, int id, ReplySupportRequest r, CancellationToken ct)
    {
        var (actor, request) = await LoadAsync(actorId, id, ct);
        if (request.Status == SupportStatus.Closed)
            throw SchedulingException.Conflict("SUPPORT_CLOSED", "Yêu cầu đã đóng. Hãy mở lại trước khi trả lời.");
        var staff = actor.Role != UserRole.Member;
        if (!staff && r.IsInternal) throw SchedulingException.Forbidden("SUPPORT_INTERNAL_STAFF_ONLY", "Chỉ nhân viên được ghi chú nội bộ.");

        var now = clock.UtcNow;
        repo.AddSupportMessage(new SupportMessage { RequestId = id, AuthorId = actorId, Body = r.Body.Trim(), IsInternal = r.IsInternal, CreatedAt = now });
        request.LastActivityAt = now;
        if (staff && !r.IsInternal)
        {
            request.HandledById ??= actorId;
            request.Status = SupportStatus.WaitingForMember;
            request.ResolvedAt = null;
            Notify(request, actorId, $"Trung tâm đã trả lời yêu cầu \"{request.Subject}\". Mở mục Hỗ trợ để xem.");
        }
        else if (!staff && request.Status is SupportStatus.WaitingForMember or SupportStatus.Resolved)
        {
            request.Status = request.HandledById is null ? SupportStatus.Open : SupportStatus.InProgress;
            request.ResolvedAt = null;
        }
        await repo.SaveChangesAsync(ct);
        return await GetAsync(actorId, id, ct);
    }

    public async Task<SupportRequestDto> UpdateStatusAsync(int actorId, int id, SupportStatus status, CancellationToken ct)
    {
        var (actor, request) = await LoadAsync(actorId, id, ct);
        if (request.Status == status) return ToDto(request);
        var staff = actor.Role != UserRole.Member;
        // Members can only close their request or reopen a finished one.
        if (!staff && !(status == SupportStatus.Closed ||
            (status == SupportStatus.Open && request.Status is SupportStatus.Resolved or SupportStatus.Closed)))
            throw SchedulingException.Forbidden("SUPPORT_STATUS_NOT_ALLOWED", "Bạn chỉ có thể đóng hoặc mở lại yêu cầu.");

        var previous = request.Status;
        var now = clock.UtcNow;
        request.Status = !staff && status == SupportStatus.Open && request.HandledById is not null ? SupportStatus.InProgress : status;
        request.LastActivityAt = now;
        request.ResolvedAt = request.Status is SupportStatus.Resolved or SupportStatus.Closed ? request.ResolvedAt ?? now : null;
        if (staff)
        {
            request.HandledById ??= actorId;
            Notify(request, actorId, $"Yêu cầu \"{request.Subject}\": {StatusLabel(request.Status)}.");
        }
        audit.AddAudit(actorId, "Support.UpdateStatus", nameof(SupportRequest), id, new { From = previous.ToString(), To = request.Status.ToString() });
        await repo.SaveChangesAsync(ct);
        return ToDto((await repo.GetSupportRequestAsync(id, ct))!);
    }

    private async Task<(User Actor, SupportRequest Request)> LoadAsync(int actorId, int id, CancellationToken ct)
    {
        var actor = await Staff.EnsureAsync(users, actorId, UserRole.Member, UserRole.Receptionist, UserRole.Manager);
        var request = await repo.GetSupportRequestAsync(id, ct);
        // Same 404 for "missing" and "someone else's" so members can't probe ids.
        if (request is null || (actor.Role == UserRole.Member && request.MemberId != actorId))
            throw SchedulingException.NotFound("SUPPORT_NOT_FOUND", "Không tìm thấy yêu cầu hỗ trợ.");
        return (actor, request);
    }

    private void Notify(SupportRequest request, int senderId, string body) => notifications.Add(new Notification
    {
        UserId = request.MemberId, SenderId = senderId, Type = NotificationType.General,
        Title = "Cập nhật yêu cầu hỗ trợ", Body = body, CreatedAt = clock.UtcNow,
    });

    private static string StatusLabel(SupportStatus s) => s switch
    {
        SupportStatus.Open => "chờ xử lý",
        SupportStatus.InProgress => "đang xử lý",
        SupportStatus.WaitingForMember => "chờ bạn phản hồi",
        SupportStatus.Resolved => "đã giải quyết",
        _ => "đã đóng",
    };

    private static SupportRequestDto ToDto(SupportRequest x) => new(x.Id, x.MemberId, x.Member.FullName, x.Member.Email, x.Category, x.Subject,
        x.Description, x.Status, x.HandledBy?.FullName, x.CreatedBy?.FullName, x.CreatedAt, x.LastActivityAt, x.ResolvedAt);
}

public sealed class PaymentHistoryService(IFrontDeskRepository repo, IUserRepository users)
{
    // Members only ever see their own; staff must name the member.
    public async Task<PagedResponse<PaymentDto>> ListAsync(int actorId, int? memberId, int page, int pageSize, CancellationToken ct)
    {
        var actor = await Staff.EnsureAsync(users, actorId, UserRole.Member, UserRole.Receptionist, UserRole.Manager);
        var target = actor.Role == UserRole.Member ? actorId
            : memberId ?? throw new SchedulingException("MEMBER_REQUIRED", "Cần chọn hội viên.");
        (page, pageSize) = Staff.Paging(page, pageSize);
        var (items, total) = await repo.ListPaymentsAsync(target, page, pageSize, ct);
        return new(items.Select(p => SubscriptionService.ToPaymentDto(p, p.Member, p.Subscription?.Plan.Name)).ToList(), total, page, pageSize);
    }
}

public sealed class ReportTrendService(IFrontDeskRepository repo, IUserRepository users, IAppClock clock)
{
    public async Task<List<ReportTrendPoint>> TrendAsync(int actorId, DateOnly from, DateOnly to, string groupBy, CancellationToken ct)
    {
        await MembershipRules.EnsureActiveRoleAsync(users, actorId, UserRole.Manager);
        if (to < from) throw new SchedulingException("REPORT_RANGE_INVALID", "Ngày kết thúc phải sau ngày bắt đầu.");
        if (to.DayNumber - from.DayNumber > 366) throw new SchedulingException("REPORT_RANGE_TOO_LONG", "Khoảng thời gian tối đa 1 năm.");
        var monthly = groupBy == "month";

        var fromUtc = clock.ToUtc(from, TimeOnly.MinValue);
        var toUtc = clock.ToUtc(to.AddDays(1), TimeOnly.MinValue);
        var offset = clock.LocalNow - clock.UtcNow;
        string Key(DateTime utc) => (utc + offset).ToString(monthly ? "yyyy-MM" : "yyyy-MM-dd");

        var periods = new List<string>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var key = d.ToString(monthly ? "yyyy-MM" : "yyyy-MM-dd");
            if (periods.Count == 0 || periods[^1] != key) periods.Add(key);
        }

        var revenue = (await repo.ListRevenueAsync(fromUtc, toUtc, ct)).GroupBy(x => Key(x.At)).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        var members = (await repo.ListNewMemberTimesAsync(fromUtc, toUtc, ct)).GroupBy(Key).ToDictionary(g => g.Key, g => g.Count());
        var bookings = (await repo.ListBookingTimesAsync(fromUtc, toUtc, ct)).GroupBy(Key).ToDictionary(g => g.Key, g => g.Count());

        return periods.Select(p => new ReportTrendPoint(p, revenue.GetValueOrDefault(p), members.GetValueOrDefault(p), bookings.GetValueOrDefault(p))).ToList();
    }

    public async Task<List<ClassFillDto>> ClassFillAsync(int actorId, CancellationToken ct)
    {
        await MembershipRules.EnsureActiveRoleAsync(users, actorId, UserRole.Manager);
        return (await repo.ListClassFillAsync(ct)).Select(c =>
        {
            // Course classes fill by enrollment; per-session classes fill by seats booked across their sessions.
            decimal? rate = c.BookingMode == ClassBookingMode.CourseEnrollment
                ? c.Capacity > 0 ? (decimal)c.ActiveEnrollments / c.Capacity : null
                : c.Sessions * c.Capacity > 0 ? (decimal)c.ActiveBookings / (c.Sessions * c.Capacity) : null;
            return new ClassFillDto(c.ClassId, c.Name, c.Coach, c.Status, c.BookingMode, c.Capacity, c.ActiveEnrollments, c.Sessions,
                c.ActiveBookings, rate is { } r ? Math.Round(r, 4) : null);
        }).ToList();
    }
}

public sealed class CoachMessageService(IFrontDeskRepository repo, IUserRepository users, INotificationRepository notifications,
    IAuditLogRepository audit, IAppClock clock)
{
    public async Task<CoachMessageResult> SendAsync(int coachId, CoachMessageRequest r, CancellationToken ct)
    {
        await MembershipRules.EnsureActiveRoleAsync(users, coachId, UserRole.Coach);
        if (r.Type is not (NotificationType.General or NotificationType.Homework))
            throw new SchedulingException("MESSAGE_TYPE_INVALID", "Chỉ gửi được thông báo chung hoặc bài tập về nhà.");
        var session = await repo.GetSessionWithClassAsync(r.SessionId, ct)
            ?? throw SchedulingException.NotFound("SESSION_NOT_FOUND", "Không tìm thấy buổi học.");
        if (session.CoachId != coachId && session.Class.CoachId != coachId)
            throw SchedulingException.Forbidden("SESSION_NOT_OWNED", "Bạn không phụ trách buổi học này.");

        var recipients = r.WholeClass
            ? await repo.ListClassMemberIdsAsync(session.ClassId, ct)
            : await repo.ListSessionMemberIdsAsync(session.Id, ct);
        if (recipients.Count == 0) throw SchedulingException.Conflict("NO_RECIPIENTS", "Chưa có học viên nào để gửi.");

        var title = r.Title.Trim();
        var body = $"{session.ClassName} • {session.Date:dd/MM/yyyy}\n{r.Body.Trim()}";
        foreach (var memberId in recipients)
            notifications.Add(new Notification { UserId = memberId, SenderId = coachId, Type = r.Type, Title = title, Body = body, CreatedAt = clock.UtcNow });
        audit.AddAudit(coachId, "Coach.SendMessage", nameof(ClassSession), session.Id, new { Type = r.Type.ToString(), r.WholeClass, Recipients = recipients.Count, Title = title });
        await repo.SaveChangesAsync(ct);
        return new(recipients.Count);
    }
}

public sealed class SubscriptionExpiryNotifier(IFrontDeskRepository repo, INotificationRepository notifications, IAppClock clock)
{
    public const int DaysAhead = 3;

    // Idempotent: one notification per subscription end date, keyed by DeduplicationKey.
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.LocalNow);
        var rows = await repo.ListExpiringSubscriptionsAsync(today, today.AddDays(DaysAhead), ct);
        if (rows.Count == 0) return 0;

        static string Key(ExpiringSubscriptionRow r) => $"sub-expiry:{r.SubscriptionId}:{r.EndDate:yyyyMMdd}";
        var existing = await repo.ExistingNotificationKeysAsync(rows.Select(Key).ToList(), ct);
        var fresh = rows.Where(r => !existing.Contains(Key(r))).ToList();
        foreach (var r in fresh)
        {
            var days = r.EndDate.DayNumber - today.DayNumber;
            notifications.AddNotification(r.MemberId, null, NotificationType.SubscriptionExpiry, $"Gói {r.PlanName} sắp hết hạn",
                $"Gói {r.PlanName} hết hạn ngày {r.EndDate:dd/MM/yyyy}{(days == 0 ? " (hôm nay)" : $" (còn {days} ngày)")}. Gia hạn tại quầy lễ tân hoặc trong mục Gói của tôi.",
                Key(r));
        }
        if (fresh.Count > 0) await repo.SaveChangesAsync(ct);
        return fresh.Count;
    }
}
