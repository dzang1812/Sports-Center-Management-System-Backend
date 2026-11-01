using System.ComponentModel.DataAnnotations;
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

// One row = one exercise (or a free note) a member did in the session. ExerciseId and Notes cannot both be empty.
public record TrainingResultInput(
    [param: Range(1, int.MaxValue)] int MemberId,
    int? ExerciseId,
    [param: Range(0, 100)] int? Sets,
    [param: Range(0, 1000)] int? Reps,
    [param: Range(0, 1000)] decimal? WeightKg,
    [param: Range(0, 600)] int? DurationMin,
    [param: StringLength(1000)] string? Notes);

// Replaces every result previously recorded for the session; an empty list clears them.
public record SaveTrainingResultsRequest([param: Required] List<TrainingResultInput> Items);

// Kind: "Class" (ClassSessionId set) or "Pt" (PtBookingId set).
public record TrainingResultDto(int Id, int MemberId, string MemberName, string Kind, int? ClassSessionId, int? PtBookingId,
    string? SessionTitle, DateOnly? SessionDate, int? ExerciseId, string? ExerciseName, int? Sets, int? Reps, decimal? WeightKg,
    int? DurationMin, string? Notes, int RecordedById, string RecordedByName, DateTime RecordedAt);

public interface ICoachTrainingService
{
    Task<List<TrainingResultDto>> ClassSessionResultsAsync(int coachId, int sessionId, CancellationToken ct);
    Task<List<TrainingResultDto>> SaveClassSessionResultsAsync(int coachId, int sessionId, SaveTrainingResultsRequest request, CancellationToken ct);
    Task<List<TrainingResultDto>> PtResultsAsync(int coachId, int bookingId, CancellationToken ct);
    Task<List<TrainingResultDto>> SavePtResultsAsync(int coachId, int bookingId, SaveTrainingResultsRequest request, CancellationToken ct);
    Task<List<TrainingResultDto>> StudentResultsAsync(int coachId, int memberId, CancellationToken ct);
    Task<List<TrainingResultDto>> MyResultsAsync(int memberId, CancellationToken ct);

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

    public async Task<List<TrainingResultDto>> ClassSessionResultsAsync(int coachId, int sessionId, CancellationToken ct)
    {
        await RequireOwnClassSessionAsync(coachId, sessionId, ct);
        return await ResultDtosAsync(sessionId, null, null, ct);
    }

    public async Task<List<TrainingResultDto>> SaveClassSessionResultsAsync(int coachId, int sessionId, SaveTrainingResultsRequest request,
        CancellationToken ct)
    {
        var session = await RequireOwnClassSessionAsync(coachId, sessionId, ct);
        if (session.Status == SessionStatus.Cancelled)
            throw SchedulingException.Conflict("SESSION_CANCELLED", "Buổi học đã bị huỷ.");
        var members = (await repo.ClassSessionMemberIdsAsync(sessionId, ct)).ToHashSet();
        await ReplaceResultsAsync(coachId, sessionId, null, members, request, ct);
        return await ResultDtosAsync(sessionId, null, null, ct);
    }

    public async Task<List<TrainingResultDto>> PtResultsAsync(int coachId, int bookingId, CancellationToken ct)
    {
        await RequireOwnPtBookingAsync(coachId, bookingId, ct);
        return await ResultDtosAsync(null, bookingId, null, ct);
    }

    public async Task<List<TrainingResultDto>> SavePtResultsAsync(int coachId, int bookingId, SaveTrainingResultsRequest request, CancellationToken ct)
    {
        var booking = await RequireOwnPtBookingAsync(coachId, bookingId, ct);
        if (booking.Status == BookingStatus.Cancelled)
            throw SchedulingException.Conflict("BOOKING_CANCELLED", "Buổi PT đã bị huỷ.");
        await ReplaceResultsAsync(coachId, null, bookingId, [booking.MemberId], request, ct);
        return await ResultDtosAsync(null, bookingId, null, ct);
    }

    public async Task<List<TrainingResultDto>> StudentResultsAsync(int coachId, int memberId, CancellationToken ct)
    {
        await RequireStudentAsync(coachId, memberId, ct);
        return await ResultDtosAsync(null, null, memberId, ct);
    }

    public Task<List<TrainingResultDto>> MyResultsAsync(int memberId, CancellationToken ct) => ResultDtosAsync(null, null, memberId, ct);

    private async Task ReplaceResultsAsync(int coachId, int? sessionId, int? ptBookingId, HashSet<int> members,
        SaveTrainingResultsRequest request, CancellationToken ct)
    {
        if (request.Items.FirstOrDefault(i => !members.Contains(i.MemberId)) is { } stranger)
            throw new SchedulingException("MEMBER_NOT_IN_SESSION", $"Hội viên #{stranger.MemberId} không có trong buổi tập này.");
        if (request.Items.Any(i => i.ExerciseId is null && string.IsNullOrWhiteSpace(i.Notes)))
            throw new SchedulingException("RESULT_EMPTY", "Mỗi dòng kết quả cần có bài tập hoặc ghi chú.");
        var exerciseIds = request.Items.Where(i => i.ExerciseId is not null).Select(i => i.ExerciseId!.Value).Distinct().ToList();
        if (await repo.CountExercisesAsync(exerciseIds, ct) != exerciseIds.Count)
            throw new SchedulingException("EXERCISE_NOT_FOUND", "Có bài tập không tồn tại.");

        repo.RemoveResults(await repo.TrackedResultsAsync(sessionId, ptBookingId, ct));
        var now = clock.UtcNow;
        repo.AddResults(request.Items.Select(i => new TrainingResult
        {
            MemberId = i.MemberId, RecordedById = coachId, SessionId = sessionId, PtBookingId = ptBookingId,
            ExerciseId = i.ExerciseId, Sets = i.Sets, Reps = i.Reps, WeightKg = i.WeightKg, DurationMin = i.DurationMin,
            Notes = string.IsNullOrWhiteSpace(i.Notes) ? null : i.Notes.Trim(), RecordedAt = now,
        }));
        await repo.SaveChangesAsync(ct);
    }

    private async Task<List<TrainingResultDto>> ResultDtosAsync(int? sessionId, int? ptBookingId, int? memberId, CancellationToken ct) =>
        (await repo.ResultsAsync(sessionId, ptBookingId, memberId, ct)).Select(r => new TrainingResultDto(r.Id, r.MemberId, r.Member.FullName,
            r.PtBookingId is null ? "Class" : "Pt", r.SessionId, r.PtBookingId,
            r.Session?.Class.Name ?? (r.PtBooking is null ? null : $"PT {r.PtBooking.Sport.Name}"),
            r.Session?.Date ?? r.PtBooking?.Date, r.ExerciseId, r.Exercise?.Name, r.Sets, r.Reps, r.WeightKg, r.DurationMin, r.Notes,
            r.RecordedById, r.RecordedBy.FullName, r.RecordedAt)).ToList();

    // Session coach, else the class coach (same rule as "who teaches whom").
    private async Task<ClassSession> RequireOwnClassSessionAsync(int coachId, int sessionId, CancellationToken ct)
    {
        await EnsureCoachAsync(coachId);
        var session = await repo.ClassSessionAsync(sessionId, ct)
            ?? throw SchedulingException.NotFound("SESSION_NOT_FOUND", "Không tìm thấy buổi học.");
        if ((session.CoachId ?? session.Class.CoachId) != coachId)
            throw SchedulingException.Forbidden("NOT_YOUR_SESSION", "Bạn không dạy buổi học này.");
        return session;
    }

    private async Task<PtBooking> RequireOwnPtBookingAsync(int coachId, int bookingId, CancellationToken ct)
    {
        await EnsureCoachAsync(coachId);
        var booking = await repo.PtBookingAsync(bookingId, ct)
            ?? throw SchedulingException.NotFound("PT_BOOKING_NOT_FOUND", "Không tìm thấy buổi PT.");
        if (booking.CoachId != coachId)
            throw SchedulingException.Forbidden("NOT_YOUR_SESSION", "Bạn không dạy buổi PT này.");
        return booking;
    }

    private async Task RequireStudentAsync(int coachId, int memberId, CancellationToken ct)
    {
        await EnsureCoachAsync(coachId);
        if (!await repo.CoachTeachesMemberAsync(coachId, memberId, ct))
            throw SchedulingException.Forbidden("NOT_YOUR_STUDENT", "Học viên này không học với bạn.");
    }

    private Task<User> EnsureCoachAsync(int coachId) => MembershipRules.EnsureActiveRoleAsync(users, coachId, UserRole.Coach);
}
