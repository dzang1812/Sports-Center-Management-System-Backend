using System.ComponentModel.DataAnnotations;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Membership;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.Services.Operations;

public record SupportRequestDto(int Id, int MemberId, string MemberName, string MemberEmail, string Subject, string Description,
    SupportStatus Status, string? HandledByName, DateTime CreatedAt, DateTime? ResolvedAt);

public record CreateSupportRequest(
    [param: Required(ErrorMessage = "Tiêu đề là bắt buộc"), StringLength(150, MinimumLength = 2, ErrorMessage = "Tiêu đề 2–150 ký tự")] string Subject,
    [param: Required(ErrorMessage = "Nội dung là bắt buộc"), StringLength(2000, MinimumLength = 2, ErrorMessage = "Nội dung 2–2000 ký tự")] string Description);

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
    public async Task<SupportRequestDto> CreateAsync(int memberId, CreateSupportRequest r, CancellationToken ct)
    {
        await MembershipRules.EnsureActiveRoleAsync(users, memberId, UserRole.Member);
        var request = new SupportRequest { MemberId = memberId, Subject = r.Subject.Trim(), Description = r.Description.Trim(), CreatedAt = clock.UtcNow };
        repo.AddSupportRequest(request);
        await repo.SaveChangesAsync(ct);
        return ToDto((await repo.GetSupportRequestAsync(request.Id, ct))!);
    }

    public async Task<PagedResponse<SupportRequestDto>> ListMineAsync(int memberId, int page, int pageSize, CancellationToken ct)
    {
        await MembershipRules.EnsureActiveRoleAsync(users, memberId, UserRole.Member);
        (page, pageSize) = Staff.Paging(page, pageSize);
        var (items, total) = await repo.ListSupportRequestsAsync(memberId, null, null, page, pageSize, ct);
        return new(items.Select(ToDto).ToList(), total, page, pageSize);
    }

    public async Task<PagedResponse<SupportRequestDto>> ListAsync(int actorId, SupportStatus? status, string? q, int page, int pageSize, CancellationToken ct)
    {
        await Staff.EnsureAsync(users, actorId, UserRole.Receptionist, UserRole.Manager);
        (page, pageSize) = Staff.Paging(page, pageSize);
        var (items, total) = await repo.ListSupportRequestsAsync(null, status, q, page, pageSize, ct);
        return new(items.Select(ToDto).ToList(), total, page, pageSize);
    }

    public async Task<SupportRequestDto> UpdateStatusAsync(int actorId, int id, SupportStatus status, CancellationToken ct)
    {
        await Staff.EnsureAsync(users, actorId, UserRole.Receptionist, UserRole.Manager);
        var request = await repo.GetSupportRequestAsync(id, ct)
            ?? throw SchedulingException.NotFound("SUPPORT_NOT_FOUND", "Không tìm thấy yêu cầu hỗ trợ.");
        if (request.Status == status) return ToDto(request);

        var previous = request.Status;
        request.Status = status;
        request.HandledById = actorId;
        request.ResolvedAt = status == SupportStatus.Resolved ? clock.UtcNow : null;
        notifications.Add(new Notification
        {
            UserId = request.MemberId,
            SenderId = actorId,
            Type = NotificationType.General,
            Title = $"Yêu cầu hỗ trợ: {request.Subject}",
            Body = $"Trạng thái: {StatusLabel(status)}.",
            CreatedAt = clock.UtcNow,
        });
        audit.AddAudit(actorId, "Support.UpdateStatus", nameof(SupportRequest), id, new { From = previous.ToString(), To = status.ToString() });
        await repo.SaveChangesAsync(ct);
        return ToDto((await repo.GetSupportRequestAsync(id, ct))!);
    }

    private static string StatusLabel(SupportStatus s) => s switch
    {
        SupportStatus.Open => "Đang chờ xử lý",
        SupportStatus.InProgress => "Đang xử lý",
        _ => "Đã giải quyết",
    };

    private static SupportRequestDto ToDto(SupportRequest x) => new(x.Id, x.MemberId, x.Member.FullName, x.Member.Email, x.Subject,
        x.Description, x.Status, x.HandledBy?.FullName, x.CreatedAt, x.ResolvedAt);
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
