using System.ComponentModel.DataAnnotations;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.Services.Membership;

public enum GymCardStatus { Active, PaymentPending, PaymentFailed, NotYetActive, Expired, Inactive, NoEntitlement, Blocked }
public enum GymScanStatus { CheckedIn, AlreadyCheckedIn, CheckedOut }

public sealed record GymCardDto(
    string CardCode,
    int MemberId,
    string MemberName,
    GymCardStatus Status,
    int? SubscriptionId,
    string? PlanName,
    DateOnly? StartDate,
    DateOnly? EndDate);

public sealed record GymScanRequest(
    [param: Required, StringLength(64, MinimumLength = 8)] string CardCode,
    [param: StringLength(100)] string? Station = null);

public sealed record GymScanResult(
    GymScanStatus Status,
    bool Accepted,
    int VisitId,
    int MemberId,
    string MemberName,
    int? SubscriptionId,
    DateTime CheckedInAt,
    DateTime? CheckedOutAt);

public sealed record GymVisitDto(
    int Id,
    int MemberId,
    int? SubscriptionId,
    string? PlanName,
    DateTime CheckedInAt,
    DateTime? CheckedOutAt,
    string CheckInSource,
    string? CheckOutSource,
    int CheckedInById,
    string CheckedInBy,
    int? CheckedOutById,
    string? CheckedOutBy);

public sealed record GymVisitHistoryResponse(List<GymVisitDto> Items, int Total, int Page, int PageSize);

public interface IGymAccessService
{
    Task<GymCardDto> GetMyCardAsync(int memberId, CancellationToken ct);
    Task<GymCardDto> LookupCardAsync(int actorId, string cardCode, CancellationToken ct);
    Task<GymScanResult> CheckInAsync(int actorId, GymScanRequest request, CancellationToken ct);
    Task<GymScanResult> CheckOutAsync(int actorId, GymScanRequest request, CancellationToken ct);
    Task<GymVisitHistoryResponse> ListVisitsAsync(
        int actorId, int? memberId, DateOnly from, DateOnly to, int page, int pageSize, CancellationToken ct);
}

public sealed class GymAccessService(
    IGymAccessRepository gym,
    IUserRepository users,
    IAuditLogRepository audit,
    ISchedulingTransaction transaction,
    IAppClock clock) : IGymAccessService
{
    public Task<GymCardDto> GetMyCardAsync(int memberId, CancellationToken ct) =>
        transaction.ExecuteAsync(async token =>
        {
            await transaction.LockAsync([new(ScheduleResourceType.Member, memberId)], token);
            var member = await RequireMemberAsync(memberId, requireActive: true, token);
            if (member.MemberProfile is null)
            {
                member.MemberProfile = new MemberProfile { UserId = member.Id };
                gym.AddMemberProfile(member.MemberProfile);
            }
            if (member.MemberProfile.GymCardCode is null)
                member.MemberProfile.GymCardCode = NewCardCode();

            var subscriptions = await gym.ListSubscriptionsAsync(member.Id, token);
            return BuildCard(member, subscriptions);
        }, ct);

    public async Task<GymCardDto> LookupCardAsync(int actorId, string cardCode, CancellationToken ct)
    {
        await EnsureReceptionistAsync(actorId);
        var member = await FindCardOwnerAsync(cardCode, ct);
        var subscriptions = await gym.ListSubscriptionsAsync(member.Id, ct);
        return BuildCard(member, subscriptions);
    }

    public async Task<GymScanResult> CheckInAsync(int actorId, GymScanRequest request, CancellationToken ct)
    {
        await EnsureReceptionistAsync(actorId);
        var cardCode = NormalizeCardCode(request.CardCode);
        var memberKey = await gym.FindMemberByCardCodeAsync(cardCode, ct)
            ?? throw SchedulingException.NotFound("GYM_CARD_NOT_FOUND", "Không tìm thấy thẻ tập.");

        return await transaction.ExecuteAsync(async token =>
        {
            await transaction.LockAsync([new(ScheduleResourceType.Member, memberKey.Id)], token);
            var member = await gym.FindMemberByCardCodeAsync(cardCode, token)
                ?? throw SchedulingException.NotFound("GYM_CARD_NOT_FOUND", "Không tìm thấy thẻ tập.");
            if (!member.IsActive)
                throw SchedulingException.Forbidden("GYM_MEMBER_BLOCKED", "Tài khoản hội viên đang bị khóa.");

            var openVisit = await gym.GetOpenVisitAsync(member.Id, token);
            if (openVisit is not null)
                return ToScanResult(GymScanStatus.AlreadyCheckedIn, false, openVisit, member);

            var subscriptions = await gym.ListSubscriptionsAsync(member.Id, token);
            var subscription = SelectActiveEntitlement(subscriptions)
                ?? throw AccessDenied(subscriptions);
            var visit = new CheckIn
            {
                MemberId = member.Id,
                SubscriptionId = subscription.Id,
                CheckedInById = actorId,
                CheckedInAt = clock.UtcNow,
                CheckInSource = NormalizeStation(request.Station),
            };
            gym.AddVisit(visit);
            await transaction.SaveChangesAsync(token);
            audit.AddAudit(actorId, "GymAccess.CheckIn", nameof(CheckIn), visit.Id,
                new { visit.MemberId, visit.SubscriptionId, visit.CheckInSource });
            return ToScanResult(GymScanStatus.CheckedIn, true, visit, member);
        }, ct);
    }

    public async Task<GymScanResult> CheckOutAsync(int actorId, GymScanRequest request, CancellationToken ct)
    {
        await EnsureReceptionistAsync(actorId);
        var cardCode = NormalizeCardCode(request.CardCode);
        var memberKey = await gym.FindMemberByCardCodeAsync(cardCode, ct)
            ?? throw SchedulingException.NotFound("GYM_CARD_NOT_FOUND", "Không tìm thấy thẻ tập.");

        return await transaction.ExecuteAsync(async token =>
        {
            await transaction.LockAsync([new(ScheduleResourceType.Member, memberKey.Id)], token);
            var member = await gym.FindMemberByCardCodeAsync(cardCode, token)
                ?? throw SchedulingException.NotFound("GYM_CARD_NOT_FOUND", "Không tìm thấy thẻ tập.");
            var visit = await gym.GetOpenVisitAsync(member.Id, token)
                ?? throw SchedulingException.Conflict("GYM_NO_OPEN_VISIT", "Hội viên không có lượt Gym đang mở.");

            visit.CheckedOutAt = clock.UtcNow;
            visit.CheckedOutById = actorId;
            visit.CheckOutSource = NormalizeStation(request.Station);
            audit.AddAudit(actorId, "GymAccess.CheckOut", nameof(CheckIn), visit.Id,
                new { visit.MemberId, visit.SubscriptionId, visit.CheckOutSource });
            return ToScanResult(GymScanStatus.CheckedOut, true, visit, member);
        }, ct);
    }

    public async Task<GymVisitHistoryResponse> ListVisitsAsync(
        int actorId, int? memberId, DateOnly from, DateOnly to, int page, int pageSize, CancellationToken ct)
    {
        ValidateRange(from, to);
        var actor = await users.GetByIdAsync(actorId)
            ?? throw SchedulingException.Forbidden("ACTOR_INACTIVE", "Tài khoản không hợp lệ hoặc đã bị khóa.");
        var targetId = actor.Role switch
        {
            UserRole.Member when memberId is null || memberId == actorId => actorId,
            UserRole.Member => throw SchedulingException.Forbidden("GYM_HISTORY_NOT_OWNED", "Không thể xem lịch sử của hội viên khác."),
            UserRole.Receptionist or UserRole.Manager when memberId is not null => memberId.Value,
            UserRole.Receptionist or UserRole.Manager => throw new SchedulingException("TARGET_MEMBER_REQUIRED", "Thiếu memberId."),
            _ => throw SchedulingException.Forbidden("ROLE_NOT_ALLOWED", "Vai trò không được xem lịch sử Gym."),
        };
        await RequireMemberAsync(targetId, requireActive: false, ct);
        page = Math.Max(1, page);
        pageSize = pageSize is 10 or 20 or 50 ? pageSize : 20;
        var fromUtc = clock.ToUtc(from, TimeOnly.MinValue);
        var toUtcExclusive = clock.ToUtc(to.AddDays(1), TimeOnly.MinValue);
        var (rows, total) = await gym.ListVisitsAsync(targetId, fromUtc, toUtcExclusive, page, pageSize, ct);
        return new(rows.Select(ToVisitDto).ToList(), total, page, pageSize);
    }

    private async Task EnsureReceptionistAsync(int actorId)
    {
        var actor = await users.GetByIdAsync(actorId);
        if (actor is null || actor.Role != UserRole.Receptionist)
            throw SchedulingException.Forbidden("RECEPTIONIST_REQUIRED", "Chỉ lễ tân được thao tác cổng Gym.");
    }

    private async Task<User> FindCardOwnerAsync(string cardCode, CancellationToken ct) =>
        await gym.FindMemberByCardCodeAsync(NormalizeCardCode(cardCode), ct)
        ?? throw SchedulingException.NotFound("GYM_CARD_NOT_FOUND", "Không tìm thấy thẻ tập.");

    private async Task<User> RequireMemberAsync(int memberId, bool requireActive, CancellationToken ct)
    {
        var member = await gym.GetMemberAsync(memberId, ct)
            ?? throw SchedulingException.NotFound("MEMBER_NOT_FOUND", "Không tìm thấy hội viên.");
        if (requireActive && !member.IsActive)
            throw SchedulingException.Forbidden("GYM_MEMBER_BLOCKED", "Tài khoản hội viên đang bị khóa.");
        return member;
    }

    private GymCardDto BuildCard(User member, List<GymSubscriptionSnapshot> subscriptions)
    {
        var selected = SelectActiveEntitlement(subscriptions);
        var status = member.IsActive ? GymCardStatus.Active : GymCardStatus.Blocked;
        if (selected is null)
        {
            selected = SelectDisplayEntitlement(subscriptions);
            status = member.IsActive ? ResolveCardStatus(subscriptions, selected) : GymCardStatus.Blocked;
        }
        return new(member.MemberProfile!.GymCardCode!, member.Id, member.FullName, status,
            selected?.Id, selected?.PlanName, selected?.StartDate, selected?.EndDate);
    }

    private GymSubscriptionSnapshot? SelectActiveEntitlement(IEnumerable<GymSubscriptionSnapshot> subscriptions)
    {
        var today = DateOnly.FromDateTime(clock.LocalNow);
        return subscriptions.Where(x => x.IncludesGymAccess && x.HasPaidPayment && x.Status == SubscriptionStatus.Active &&
                x.StartDate <= today && today <= x.EndDate)
            .OrderBy(x => x.EndDate).ThenBy(x => x.Id).FirstOrDefault();
    }

    private GymSubscriptionSnapshot? SelectDisplayEntitlement(IEnumerable<GymSubscriptionSnapshot> subscriptions) =>
        subscriptions.Where(x => x.IncludesGymAccess)
            .OrderByDescending(x => x.HasPendingPayment || x.Status == SubscriptionStatus.Pending)
            .ThenByDescending(x => x.Status == SubscriptionStatus.Active)
            .ThenByDescending(x => x.EndDate).ThenByDescending(x => x.Id).FirstOrDefault();

    private GymCardStatus ResolveCardStatus(List<GymSubscriptionSnapshot> subscriptions, GymSubscriptionSnapshot? selected)
    {
        if (selected is null) return GymCardStatus.NoEntitlement;
        var today = DateOnly.FromDateTime(clock.LocalNow);
        if (selected.HasFailedPayment && !selected.HasPaidPayment) return GymCardStatus.PaymentFailed;
        if (!selected.HasPaidPayment || selected.HasPendingPayment || selected.Status == SubscriptionStatus.Pending)
            return GymCardStatus.PaymentPending;
        if (selected.Status != SubscriptionStatus.Active) return GymCardStatus.Inactive;
        if (selected.StartDate > today) return GymCardStatus.NotYetActive;
        return selected.EndDate < today ? GymCardStatus.Expired : GymCardStatus.Inactive;
    }

    private SchedulingException AccessDenied(List<GymSubscriptionSnapshot> subscriptions)
    {
        var gymSubscriptions = subscriptions.Where(x => x.IncludesGymAccess).ToList();
        if (gymSubscriptions.Count == 0)
            return SchedulingException.Conflict("GYM_ACCESS_NOT_INCLUDED", "Hội viên không có gói bao gồm quyền Gym tự do.");
        var today = DateOnly.FromDateTime(clock.LocalNow);
        if (gymSubscriptions.Any(x => x.HasPendingPayment || x.Status == SubscriptionStatus.Pending))
            return SchedulingException.Conflict("GYM_PAYMENT_REQUIRED", "Gói Gym chưa được thanh toán hoặc xác nhận.");
        if (gymSubscriptions.Any(x => x.HasFailedPayment && !x.HasPaidPayment))
            return SchedulingException.Conflict("GYM_PAYMENT_FAILED", "Thanh toán gói Gym đã thất bại.");
        if (gymSubscriptions.Any(x => !x.HasPaidPayment))
            return SchedulingException.Conflict("GYM_PAYMENT_REQUIRED", "Gói Gym chưa được thanh toán hoặc xác nhận.");
        if (gymSubscriptions.Any(x => x.Status == SubscriptionStatus.Active && x.StartDate > today))
            return SchedulingException.Conflict("GYM_ACCESS_NOT_STARTED", "Gói Gym chưa đến ngày hiệu lực.");
        if (gymSubscriptions.Any(x => x.HasPaidPayment && x.EndDate < today))
            return SchedulingException.Conflict("GYM_ACCESS_EXPIRED", "Gói Gym đã hết hạn.");
        return SchedulingException.Conflict("GYM_SUBSCRIPTION_INACTIVE", "Gói Gym đã bị hủy hoặc không còn hoạt động.");
    }

    private static string NewCardCode() => $"GYM-{Guid.NewGuid():N}".ToUpperInvariant();
    private static string NormalizeCardCode(string cardCode) => cardCode.Trim().ToUpperInvariant();
    private static string NormalizeStation(string? station) => string.IsNullOrWhiteSpace(station) ? "ReceptionDesk" : station.Trim();

    private static GymScanResult ToScanResult(GymScanStatus status, bool accepted, CheckIn visit, User member) =>
        new(status, accepted, visit.Id, member.Id, member.FullName, visit.SubscriptionId,
            visit.CheckedInAt, visit.CheckedOutAt);

    private static GymVisitDto ToVisitDto(CheckIn x) => new(
        x.Id, x.MemberId, x.SubscriptionId, x.Subscription?.Plan.Name, x.CheckedInAt, x.CheckedOutAt,
        x.CheckInSource, x.CheckOutSource, x.CheckedInById, x.CheckedInBy.FullName,
        x.CheckedOutById, x.CheckedOutBy?.FullName);

    private static void ValidateRange(DateOnly from, DateOnly to)
    {
        if (to < from || to.DayNumber - from.DayNumber > 366)
            throw new SchedulingException("INVALID_DATE_RANGE", "Khoảng lịch sử Gym tối đa 366 ngày.");
    }
}
