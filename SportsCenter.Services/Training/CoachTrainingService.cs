using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Membership;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.Services.Training;

public record CoachStudentDto(int MemberId, string FullName, string Email, RosterMemberProfile Profile);
public record CoachClassDto(int ClassId, string Name, string? Sport, ClassStatus Status);

// Exactly one of MemberId / ClassId on create; the target cannot change on update.
public class CoachSavePlanDto : SavePlanDto
{
    public int? MemberId { get; set; }
    public int? ClassId { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
}

public interface ICoachTrainingService
{
    Task<List<CoachStudentDto>> StudentsAsync(int coachId, string? search, CancellationToken ct);
    Task<List<CoachClassDto>> ClassesAsync(int coachId, CancellationToken ct);
    Task<List<PlanDto>> PlansAsync(int coachId, int? memberId, int? classId, CancellationToken ct);
    Task<PlanDto> SavePlanAsync(int coachId, int? planId, CoachSavePlanDto dto, CancellationToken ct);
    Task DeletePlanAsync(int coachId, int planId, CancellationToken ct);
}

public sealed class CoachTrainingService(ITrainingRepository repo, IUserRepository users, IAppClock clock) : ICoachTrainingService
{
    public async Task<List<CoachStudentDto>> StudentsAsync(int coachId, string? search, CancellationToken ct)
    {
        await EnsureCoachAsync(coachId);
        return (await repo.CoachStudentsAsync(coachId, search, ct)).Select(u => new CoachStudentDto(u.Id, u.FullName, u.Email,
            new RosterMemberProfile(u.Phone, u.Gender, u.DateOfBirth, u.MemberProfile?.HeightCm, u.MemberProfile?.WeightKg,
                u.MemberProfile?.Goal, u.MemberProfile?.Level))).ToList();
    }

    public async Task<List<CoachClassDto>> ClassesAsync(int coachId, CancellationToken ct)
    {
        await EnsureCoachAsync(coachId);
        return (await repo.CoachClassesAsync(coachId, ct)).Select(c => new CoachClassDto(c.Id, c.Name, c.Sport?.Name, c.Status)).ToList();
    }

    public async Task<List<PlanDto>> PlansAsync(int coachId, int? memberId, int? classId, CancellationToken ct)
    {
        await EnsureCoachAsync(coachId);
        return (await repo.CoachPlansAsync(coachId, memberId, classId, ct)).Select(TrainingService.ToDto).ToList();
    }

    public async Task<PlanDto> SavePlanAsync(int coachId, int? planId, CoachSavePlanDto dto, CancellationToken ct)
    {
        await EnsureCoachAsync(coachId);
        await TrainingService.ValidateContentAsync(repo, dto, ct);
        if (dto.StartDate is { } s && dto.EndDate is { } e && e < s)
            throw new SchedulingException("PLAN_DATES_INVALID", "Ngày kết thúc phải sau ngày bắt đầu.");

        TrainingPlan plan;
        if (planId is null)
        {
            if ((dto.MemberId is null) == (dto.ClassId is null))
                throw new SchedulingException("PLAN_TARGET_REQUIRED", "Chọn đúng một đối tượng: một học viên hoặc một lớp.");
            if (dto.MemberId is { } memberId && !await repo.CoachTeachesMemberAsync(coachId, memberId, ct))
                throw SchedulingException.Forbidden("NOT_YOUR_STUDENT", "Học viên này không học với bạn.");
            if (dto.ClassId is { } classId && !await repo.CoachTeachesClassAsync(coachId, classId, ct))
                throw SchedulingException.Forbidden("NOT_YOUR_CLASS", "Bạn không phụ trách lớp này.");
            plan = new TrainingPlan { CoachId = coachId, MemberId = dto.MemberId, ClassId = dto.ClassId };
            repo.AddPlan(plan);
        }
        else
        {
            plan = await repo.CoachOwnedPlanAsync(coachId, planId.Value, ct)
                ?? throw SchedulingException.NotFound("PLAN_NOT_FOUND", "Không tìm thấy kế hoạch tập do bạn lập.");
        }

        TrainingService.ApplyContent(plan, dto);
        plan.StartDate = dto.StartDate ?? (planId is null ? DateOnly.FromDateTime(clock.LocalNow) : plan.StartDate);
        plan.EndDate = dto.EndDate;
        plan.IsActive = dto.Activate;
        await repo.SaveChangesAsync(ct);
        return TrainingService.ToDto((await repo.CoachPlansAsync(coachId, null, null, ct)).First(p => p.Id == plan.Id));
    }

    public async Task DeletePlanAsync(int coachId, int planId, CancellationToken ct)
    {
        await EnsureCoachAsync(coachId);
        var plan = await repo.CoachOwnedPlanAsync(coachId, planId, ct)
            ?? throw SchedulingException.NotFound("PLAN_NOT_FOUND", "Không tìm thấy kế hoạch tập do bạn lập.");
        repo.RemovePlan(plan);
        await repo.SaveChangesAsync(ct);
    }

    private Task<User> EnsureCoachAsync(int coachId) => MembershipRules.EnsureActiveRoleAsync(users, coachId, UserRole.Coach);
}
