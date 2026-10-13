using System.ComponentModel.DataAnnotations;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.Services.Membership;

public record PlanDto(int Id, string Name, string? Description, int DurationDays, decimal Price, bool IsActive,
    int? SportId, bool IncludesClasses, int PtSessionCredits, bool IncludesGymAccess, int DurationMonths);

public record UpsertPlanRequest(
    [param: Required(ErrorMessage = "Tên gói là bắt buộc"), StringLength(100, MinimumLength = 2)] string Name,
    [param: StringLength(500)] string? Description,
    [param: Range(0, 3650, ErrorMessage = "Thời hạn 0-3650 ngày")] int DurationDays,
    [param: Range(0, 1_000_000_000, ErrorMessage = "Học phí không hợp lệ")] decimal Price,
    bool IsActive,
    int? SportId,
    bool IncludesClasses,
    [param: Range(0, 1000, ErrorMessage = "Số buổi PT 0-1000")] int PtSessionCredits,
    bool IncludesGymAccess = false,
    [param: Range(0, 120, ErrorMessage = "Thời hạn 0-120 tháng")] int DurationMonths = 0);

public interface IMembershipPlanService
{
    Task<List<PlanDto>> ListAsync(bool activeOnly, CancellationToken ct);
    Task<PlanDto> CreateAsync(int actorId, UpsertPlanRequest request, CancellationToken ct);
    Task<PlanDto> UpdateAsync(int actorId, int id, UpsertPlanRequest request, CancellationToken ct);
}

public sealed class MembershipPlanService(IMembershipRepository repo, IUserRepository users) : IMembershipPlanService
{
    public async Task<List<PlanDto>> ListAsync(bool activeOnly, CancellationToken ct) =>
        (await repo.ListPlansAsync(activeOnly, ct)).Select(ToDto).ToList();

    public async Task<PlanDto> CreateAsync(int actorId, UpsertPlanRequest request, CancellationToken ct)
    {
        ValidateDuration(request);
        await MembershipRules.EnsureActiveRoleAsync(users, actorId, UserRole.Manager);
        var name = request.Name.Trim();
        if (await repo.PlanNameExistsAsync(name, ct))
            throw SchedulingException.Conflict("PLAN_NAME_EXISTS", "Tên gói đã tồn tại.");

        var plan = new MembershipPlan
        {
            Name = name, Description = request.Description?.Trim(), DurationDays = request.DurationDays,
            DurationMonths = request.DurationMonths,
            Price = request.Price, IsActive = request.IsActive, SportId = request.SportId,
            IncludesClasses = request.IncludesClasses, IncludesGymAccess = request.IncludesGymAccess,
            PtSessionCredits = request.PtSessionCredits,
        };
        await repo.AddPlanAsync(plan, ct);
        await repo.SaveChangesAsync(ct);
        await repo.AddAuditAsync(actorId, "MembershipPlan.Create", nameof(MembershipPlan), plan.Id, request, ct);
        await repo.SaveChangesAsync(ct);
        return ToDto(plan);
    }

    public async Task<PlanDto> UpdateAsync(int actorId, int id, UpsertPlanRequest request, CancellationToken ct)
    {
        ValidateDuration(request);
        await MembershipRules.EnsureActiveRoleAsync(users, actorId, UserRole.Manager);
        var plan = await repo.GetPlanAsync(id, ct) ?? throw SchedulingException.NotFound("PLAN_NOT_FOUND", "Không tìm thấy gói thành viên.");
        var name = request.Name.Trim();
        if (await repo.PlanNameExistsForOtherAsync(name, id, ct))
            throw SchedulingException.Conflict("PLAN_NAME_EXISTS", "Tên gói đã tồn tại.");

        plan.Name = name; plan.Description = request.Description?.Trim(); plan.DurationDays = request.DurationDays;
        plan.DurationMonths = request.DurationMonths;
        plan.Price = request.Price; plan.IsActive = request.IsActive; plan.SportId = request.SportId;
        plan.IncludesClasses = request.IncludesClasses; plan.IncludesGymAccess = request.IncludesGymAccess;
        plan.PtSessionCredits = request.PtSessionCredits;
        await repo.AddAuditAsync(actorId, "MembershipPlan.Update", nameof(MembershipPlan), id, request, ct);
        await repo.SaveChangesAsync(ct);
        return ToDto(plan);
    }

    private static PlanDto ToDto(MembershipPlan x) => new(x.Id, x.Name, x.Description, x.DurationDays, x.Price, x.IsActive,
        x.SportId, x.IncludesClasses, x.PtSessionCredits, x.IncludesGymAccess, x.DurationMonths);

    private static void ValidateDuration(UpsertPlanRequest request)
    {
        if ((request.DurationDays > 0) == (request.DurationMonths > 0))
            throw new SchedulingException("PLAN_DURATION_INVALID", "Gói phải dùng thời hạn theo ngày hoặc theo tháng, không đồng thời cả hai.");
    }
}
