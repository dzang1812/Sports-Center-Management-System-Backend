using System.ComponentModel.DataAnnotations;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.Services.Membership;

public record PlanSportDto(int Id, string Name);

public record ChangePlanStatusRequest([param: Required] bool? IsActive);

public record PlanDto(int Id, string Name, string? Description, int DurationDays, decimal Price, bool IsActive,
    int? SportId, bool IncludesClasses, int PtSessionCredits, bool IncludesGymAccess, int DurationMonths,
    string PlanType, string? SportName, IReadOnlyList<PlanSportDto> AllowedSports, string? ImageUrl, bool WalletPtPackage);

public record UpsertPlanRequest(
    [param: Required(ErrorMessage = "Tên gói là bắt buộc"), StringLength(100, MinimumLength = 2)] string Name,
    [param: StringLength(500)] string? Description,
    [param: Range(0, 3650, ErrorMessage = "Thời hạn 0-3650 ngày")] int DurationDays,
    [param: Range(0, 1_000_000_000, ErrorMessage = "Học phí không hợp lệ")] decimal Price,
    bool IsActive,
    int? SportId,
    bool IncludesClasses,
    [param: Range(0, 1000, ErrorMessage = "Số buổi PT 0-1000")] int PtSessionCredits,
    bool? IncludesGymAccess = null,
    [param: Range(0, 120, ErrorMessage = "Thời hạn 0-120 tháng")] int DurationMonths = 0,
    string? ImageUrl = null);

public interface IMembershipPlanService
{
    Task<List<PlanDto>> ListAsync(bool activeOnly, CancellationToken ct);
    Task<PlanDto> GetAsync(int id, bool includeInactive, CancellationToken ct);
    Task<PlanDto> CreateAsync(int actorId, UpsertPlanRequest request, CancellationToken ct);
    Task<PlanDto> UpdateAsync(int actorId, int id, UpsertPlanRequest request, CancellationToken ct);
    Task<PlanDto> SetActiveAsync(int actorId, int id, bool isActive, CancellationToken ct);
}

public sealed class MembershipPlanService(IMembershipRepository repo, IUserRepository users,
    ISchedulingTransaction transaction) : IMembershipPlanService
{
    public async Task<List<PlanDto>> ListAsync(bool activeOnly, CancellationToken ct)
    {
        var sports = await repo.ListSportsAsync(ct);
        return (await repo.ListPlansAsync(activeOnly, ct))
            .Select(x => MembershipPlanPresentation.ToDto(x, sports)).ToList();
    }

    public async Task<PlanDto> GetAsync(int id, bool includeInactive, CancellationToken ct)
    {
        var plan = await repo.GetPlanAsync(id, ct);
        if (plan is null || !includeInactive && !plan.IsActive)
            throw SchedulingException.NotFound("PLAN_NOT_FOUND", "Không tìm thấy gói thành viên.");
        return MembershipPlanPresentation.ToDto(plan, await repo.ListSportsAsync(ct));
    }

    public async Task<PlanDto> CreateAsync(int actorId, UpsertPlanRequest request, CancellationToken ct)
    {
        await MembershipRules.EnsureActiveRoleAsync(users, actorId, UserRole.Manager);
        return await transaction.ExecuteAsync(async token =>
        {
            await transaction.LockAsync([new(ScheduleResourceType.MembershipPlan, 0)], token);
            var sports = await ValidateRequestAsync(request, null, token);
            var name = request.Name.Trim();
            if (await repo.PlanNameExistsAsync(name, token))
                throw SchedulingException.Conflict("PLAN_NAME_EXISTS", "Tên gói đã tồn tại.");

            var plan = new MembershipPlan();
            Apply(plan, request, name);
            plan.WalletPtPackage = plan.PtSessionCredits > 0 && !plan.IncludesClasses && !plan.IncludesGymAccess;
            await repo.AddPlanAsync(plan, token);
            await transaction.SaveChangesAsync(token);
            await repo.AddAuditAsync(actorId, "MembershipPlan.Create", nameof(MembershipPlan), plan.Id, request, token);
            return MembershipPlanPresentation.ToDto(plan, sports);
        }, ct);
    }

    public async Task<PlanDto> UpdateAsync(int actorId, int id, UpsertPlanRequest request, CancellationToken ct)
    {
        await MembershipRules.EnsureActiveRoleAsync(users, actorId, UserRole.Manager);
        return await transaction.ExecuteAsync(async token =>
        {
            await transaction.LockAsync([
                new(ScheduleResourceType.MembershipPlan, 0), new(ScheduleResourceType.MembershipPlan, id),
            ], token);
            var plan = await repo.GetPlanAsync(id, token)
                ?? throw SchedulingException.NotFound("PLAN_NOT_FOUND", "Không tìm thấy gói thành viên.");
            var sports = await ValidateRequestAsync(request, plan, token);
            var name = request.Name.Trim();
            if (await repo.PlanNameExistsForOtherAsync(name, id, token))
                throw SchedulingException.Conflict("PLAN_NAME_EXISTS", "Tên gói đã tồn tại.");
            if (await repo.PlanHasSubscriptionsAsync(id, token) && EntitlementsChanged(plan, request))
                throw SchedulingException.Conflict("PLAN_ENTITLEMENTS_LOCKED",
                    "Không thể thay đổi quyền lợi của gói đã có người mua; hãy ngừng bán và tạo gói mới.");

            if (plan.WalletPtPackage && (request.IncludesClasses || request.IncludesGymAccess == true || request.PtSessionCredits <= 0))
                throw SchedulingException.Conflict("PLAN_PURCHASE_MODE_IMMUTABLE", "Gói PT trả ví không đổi thành dịch vụ khác; tạo gói mới.");
            var wasActive = plan.IsActive;
            Apply(plan, request, name);
            var action = wasActive && !plan.IsActive ? "MembershipPlan.Deactivate" : "MembershipPlan.Update";
            await repo.AddAuditAsync(actorId, action, nameof(MembershipPlan), id, request, token);
            return MembershipPlanPresentation.ToDto(plan, sports);
        }, ct);
    }

    public async Task<PlanDto> SetActiveAsync(int actorId, int id, bool isActive, CancellationToken ct)
    {
        var actor = await MembershipRules.EnsureActiveRoleAsync(users, actorId, UserRole.Manager);
        if (!actor.IsActive)
            throw SchedulingException.Forbidden("ACTOR_NOT_ALLOWED", "Tài khoản không hợp lệ hoặc không có quyền.");
        return await transaction.ExecuteAsync(async token =>
        {
            await transaction.LockAsync([
                new(ScheduleResourceType.MembershipPlan, 0), new(ScheduleResourceType.MembershipPlan, id),
            ], token);
            var plan = await repo.GetPlanAsync(id, token)
                ?? throw SchedulingException.NotFound("PLAN_NOT_FOUND", "Không tìm thấy gói thành viên.");
            if (plan.IsActive == isActive)
                return MembershipPlanPresentation.ToDto(plan, await repo.ListSportsAsync(token));

            // Old packages may have invalid/missing benefits. They must still be removable from sale.
            // Reopening validates the saved package without changing any purchased entitlements.
            var sports = isActive
                ? await ValidateRequestAsync(new(plan.Name, plan.Description, plan.DurationDays, plan.Price,
                    true, plan.SportId, plan.IncludesClasses, plan.PtSessionCredits, plan.IncludesGymAccess,
                    plan.DurationMonths, plan.ImageUrl), plan, token)
                : await repo.ListSportsAsync(token);
            var wasActive = plan.IsActive;
            plan.IsActive = isActive;
            await repo.AddAuditAsync(actorId, isActive ? "MembershipPlan.Activate" : "MembershipPlan.Deactivate",
                nameof(MembershipPlan), id, new { WasActive = wasActive, IsActive = isActive }, token);
            return MembershipPlanPresentation.ToDto(plan, sports);
        }, ct);
    }

    private async Task<List<Sport>> ValidateRequestAsync(UpsertPlanRequest request, MembershipPlan? existing,
        CancellationToken ct)
    {
        ValidateDuration(request);
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new SchedulingException("PLAN_NAME_REQUIRED", "Tên gói là bắt buộc.");
        if (existing?.WalletPtPackage == true || existing is null && request.PtSessionCredits > 0 && !request.IncludesClasses && request.IncludesGymAccess != true)
            SportsCenter.Services.Payments.WalletService.ValidateMoney(request.Price);
        if (request.Price < 0) throw new SchedulingException("PLAN_PRICE_INVALID", "Giá gói không được âm.");
        var includesGymAccess = request.IncludesGymAccess ?? existing?.IncludesGymAccess ?? false;
        if (!request.IncludesClasses && !includesGymAccess && request.PtSessionCredits <= 0)
            throw new SchedulingException("PLAN_ENTITLEMENT_REQUIRED", "Gói phải có ít nhất một quyền lợi.");

        var sports = await repo.ListSportsAsync(ct);
        if (request.SportId is { } sportId)
        {
            var sport = sports.FirstOrDefault(x => x.Id == sportId)
                ?? throw new SchedulingException("SPORT_NOT_FOUND", "Bộ môn không tồn tại.");
            if (sport.Status != SportStatus.Active)
                throw SchedulingException.Conflict("SPORT_INACTIVE", "Bộ môn đã ngừng hoạt động.");
            if (request.IncludesClasses && !sport.HasClasses)
                throw new SchedulingException("SPORT_CLASSES_UNSUPPORTED", "Bộ môn không hỗ trợ lớp nhóm.");
            if (request.PtSessionCredits > 0 && !sport.HasPt)
                throw new SchedulingException("SPORT_PT_UNSUPPORTED", "Bộ môn không hỗ trợ PT.");
        }
        else
        {
            if (request.IncludesClasses && !sports.Any(x => x.Status == SportStatus.Active && x.HasClasses))
                throw SchedulingException.Conflict("NO_ELIGIBLE_CLASS_SPORT", "Không có bộ môn lớp nhóm đang hoạt động.");
            if (request.PtSessionCredits > 0 && !sports.Any(x => x.Status == SportStatus.Active && x.HasPt))
                throw SchedulingException.Conflict("NO_ELIGIBLE_PT_SPORT", "Không có bộ môn PT đang hoạt động.");
        }
        return sports;
    }

    private static void Apply(MembershipPlan plan, UpsertPlanRequest request, string name)
    {
        plan.Name = name;
        plan.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        plan.DurationDays = request.DurationDays;
        plan.DurationMonths = request.DurationMonths;
        plan.Price = request.Price;
        plan.IsActive = request.IsActive;
        plan.SportId = request.SportId;
        plan.IncludesClasses = request.IncludesClasses;
        plan.IncludesGymAccess = request.IncludesGymAccess ?? plan.IncludesGymAccess;
        plan.PtSessionCredits = request.PtSessionCredits;
        plan.ImageUrl = request.ImageUrl;
    }

    private static bool EntitlementsChanged(MembershipPlan plan, UpsertPlanRequest request) =>
        plan.SportId != request.SportId || plan.IncludesClasses != request.IncludesClasses ||
        request.IncludesGymAccess.HasValue && plan.IncludesGymAccess != request.IncludesGymAccess.Value ||
        plan.PtSessionCredits != request.PtSessionCredits;

    private static void ValidateDuration(UpsertPlanRequest request)
    {
        if ((request.DurationDays > 0) == (request.DurationMonths > 0))
            throw new SchedulingException("PLAN_DURATION_INVALID",
                "Gói phải dùng thời hạn theo ngày hoặc theo tháng, không đồng thời cả hai.");
    }
}

internal static class MembershipPlanPresentation
{
    public static PlanDto ToDto(MembershipPlan plan, IReadOnlyCollection<Sport> sports) => new(
        plan.Id, plan.Name, plan.Description, plan.DurationDays, plan.Price, plan.IsActive, plan.SportId,
        plan.IncludesClasses, plan.PtSessionCredits, plan.IncludesGymAccess, plan.DurationMonths,
        GetPlanType(plan), plan.Sport?.Name ?? sports.FirstOrDefault(x => x.Id == plan.SportId)?.Name,
        GetAllowedSports(plan, sports).Select(x => new PlanSportDto(x.Id, x.Name)).ToList(), plan.ImageUrl, plan.WalletPtPackage);

    public static string GetPlanType(MembershipPlan plan) => plan.PtSessionCredits switch
    {
        > 0 when plan.IncludesClasses || plan.IncludesGymAccess => "Hybrid",
        > 0 => "PersonalTraining",
        _ => "TimeBased",
    };

    public static List<Sport> GetAllowedSports(MembershipPlan plan, IReadOnlyCollection<Sport> sports)
    {
        if (plan.SportId is { } sportId) return sports.Where(x => x.Id == sportId).ToList();
        return sports.Where(x => x.Status == SportStatus.Active &&
            (plan.IncludesClasses && x.HasClasses || plan.PtSessionCredits > 0 && x.HasPt)).ToList();
    }
}
