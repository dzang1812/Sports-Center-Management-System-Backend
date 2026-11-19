using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Auth;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.Services.Training;

public record ExerciseDto(int Id, string Name, string? MuscleGroup, string Level, string? Description, string? Equipment,
    int? DefaultSets, string? DefaultReps, int? RestSeconds, string? ImageUrl, string? VideoUrl);

public class SavePlanItemDto
{
    public int ExerciseId { get; set; }
    [Range(1, 20)] public int Sets { get; set; } = 3;
    [MaxLength(20)] public string? Reps { get; set; }
    [MaxLength(300)] public string? Notes { get; set; }
}

public class SavePlanSessionDto
{
    public DayOfWeek DayOfWeek { get; set; }
    [MaxLength(100)] public string Focus { get; set; } = "";
    public List<SavePlanItemDto> Items { get; set; } = [];
}

public class SavePlanDto
{
    [Required, MaxLength(100)] public string Title { get; set; } = "";
    [MaxLength(100)] public string? Goal { get; set; }
    [MaxLength(500)] public string? Description { get; set; }
    public bool Activate { get; set; } = true;
    public List<SavePlanSessionDto> Sessions { get; set; } = [];
}

public record PlanItemDto(int ExerciseId, string ExerciseName, string? MuscleGroup, string? ImageUrl, int? Sets, string? Reps, string? Notes);
public record PlanSessionDto(int Id, DayOfWeek DayOfWeek, string Focus, List<PlanItemDto> Items);
// CoachId set = made by a coach (read-only for the member); then exactly one of MemberId / ClassId is the target.
public record PlanDto(int Id, string Title, string? Goal, string? Description, int DaysPerWeek, bool IsActive, bool CreatedByAi,
    DateTime CreatedAt, List<PlanSessionDto> Sessions, int? CoachId = null, string? CoachName = null, int? MemberId = null,
    string? MemberName = null, int? ClassId = null, string? ClassName = null, DateOnly? StartDate = null, DateOnly? EndDate = null);

public record ExerciseMediaDto(string Name, string? ImageUrl, string? VideoUrl);

public interface ITrainingService
{
    Task<List<ExerciseDto>> ExercisesAsync(string? muscleGroup, string? search, CancellationToken ct);
    Task<List<string>> MuscleGroupsAsync(CancellationToken ct);
    Task<int> SeedExercisesAsync(string seedFile, CancellationToken ct);
    Task<int> ImportMediaAsync(IEnumerable<ExerciseMediaDto> media, CancellationToken ct);
    Task<List<PlanDto>> PlansAsync(int memberId, CancellationToken ct);
    Task<PlanDto> SavePlanAsync(int memberId, int? planId, SavePlanDto dto, CancellationToken ct);
    Task ActivateAsync(int memberId, int planId, CancellationToken ct);
    /// <summary>Copies a coach-made plan into the member's own plans and makes it the active one (reuses the copy on a second call).</summary>
    Task<PlanDto> UsePlanAsync(int memberId, int planId, CancellationToken ct);
    Task DeleteAsync(int memberId, int planId, CancellationToken ct);
}

public class TrainingService(ITrainingRepository repo, IAppClock clock) : ITrainingService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed record SeedRow(string Name, string? Equipment, string Level, int? Sets, string? Reps, int? Rest, string? MuscleGroup, string? Description, string? ImageUrl);

    public async Task<List<ExerciseDto>> ExercisesAsync(string? muscleGroup, string? search, CancellationToken ct) =>
        (await repo.ExercisesAsync(muscleGroup, search, ct)).Select(x => new ExerciseDto(x.Id, x.Name, x.MuscleGroup,
            x.Level.ToString(), x.Description, x.Equipment, x.DefaultSets, x.DefaultReps, x.RestSeconds, x.ImageUrl, x.VideoUrl)).ToList();

    public Task<List<string>> MuscleGroupsAsync(CancellationToken ct) => repo.MuscleGroupsAsync(ct);

    /// <summary>Adds the bundled exercises that are not in the table yet (matched by name); safe to run repeatedly.</summary>
    public async Task<int> SeedExercisesAsync(string seedFile, CancellationToken ct)
    {
        var rows = JsonSerializer.Deserialize<List<SeedRow>>(await File.ReadAllTextAsync(seedFile, ct), Json) ?? [];
        var known = (await repo.TrackedExercisesAsync(ct)).ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var r in rows)
        {
            if (known.TryGetValue(r.Name, out var existing))
            {
                existing.ImageUrl ??= r.ImageUrl; // back-fill rows seeded before the images were known
                continue;
            }
            repo.AddExercise(new Exercise
            {
                Name = r.Name, Equipment = r.Equipment, MuscleGroup = r.MuscleGroup, Description = r.Description,
                Level = Enum.TryParse<Level>(r.Level, true, out var lv) ? lv : Level.Beginner,
                DefaultSets = r.Sets, DefaultReps = r.Reps, RestSeconds = r.Rest, ImageUrl = r.ImageUrl,
            });
            added++;
        }
        await repo.SaveChangesAsync(ct);
        return added;
    }

    /// <summary>Attaches Cloudinary image/video URLs (exported from the old GymSupport DB) to exercises by name.</summary>
    public async Task<int> ImportMediaAsync(IEnumerable<ExerciseMediaDto> media, CancellationToken ct)
    {
        var byName = (await repo.TrackedExercisesAsync(ct)).ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var updated = 0;
        foreach (var m in media)
        {
            if (!byName.TryGetValue(m.Name.Trim(), out var ex)) continue;
            if (!string.IsNullOrWhiteSpace(m.ImageUrl)) ex.ImageUrl = m.ImageUrl.Trim();
            if (!string.IsNullOrWhiteSpace(m.VideoUrl)) ex.VideoUrl = m.VideoUrl.Trim();
            updated++;
        }
        await repo.SaveChangesAsync(ct);
        return updated;
    }

    public async Task<List<PlanDto>> PlansAsync(int memberId, CancellationToken ct) =>
        (await repo.PlansAsync(memberId, ct)).Select(ToDto).ToList();

    public async Task<PlanDto> SavePlanAsync(int memberId, int? planId, SavePlanDto dto, CancellationToken ct)
    {
        await ValidateContentAsync(repo, dto, ct);

        TrainingPlan plan;
        if (planId is null)
        {
            plan = new TrainingPlan { MemberId = memberId, StartDate = DateOnly.FromDateTime(clock.LocalNow) };
            repo.AddPlan(plan);
        }
        else
        {
            plan = await repo.OwnedPlanAsync(memberId, planId.Value, ct) ?? throw new AuthException("Không tìm thấy lịch tập.");
        }
        ApplyContent(plan, dto);

        if (dto.Activate) { await repo.DeactivatePlansAsync(memberId, ct); plan.IsActive = true; }
        else if (planId is null) plan.IsActive = false;
        await repo.SaveChangesAsync(ct);
        return ToDto((await repo.PlansAsync(memberId, ct)).First(p => p.Id == plan.Id));
    }

    public async Task ActivateAsync(int memberId, int planId, CancellationToken ct)
    {
        var plan = await repo.OwnedPlanAsync(memberId, planId, ct) ?? throw new AuthException("Không tìm thấy lịch tập.");
        if (plan.IsActive) return; // already in use; deactivating first would leave it off because the tracked flag does not change
        await repo.DeactivatePlansAsync(memberId, ct);
        plan.IsActive = true;
        await repo.SaveChangesAsync(ct);
    }

    public async Task<PlanDto> UsePlanAsync(int memberId, int planId, CancellationToken ct)
    {
        var plans = await repo.PlansAsync(memberId, ct);
        var source = plans.FirstOrDefault(p => p.Id == planId && p.CoachId != null)
            ?? throw new AuthException("Không tìm thấy giáo án của HLV.");

        // The copy remembers its source in the description, so using the same plan twice does not create duplicates.
        var marker = $"Từ giáo án của HLV (#{source.Id})";
        var copy = plans.FirstOrDefault(p => p.CoachId == null && p.Description != null && p.Description.StartsWith(marker));
        if (copy is not null)
        {
            await ActivateAsync(memberId, copy.Id, ct);
            return ToDto((await repo.PlansAsync(memberId, ct)).First(p => p.Id == copy.Id));
        }

        var description = $"{marker} {source.Coach?.FullName}. {source.Description}".Trim();
        return await SavePlanAsync(memberId, null, new SavePlanDto
        {
            Title = source.Title,
            Goal = source.Goal,
            Description = description.Length > 500 ? description[..500] : description,
            Activate = true,
            Sessions = [.. source.Sessions.Select(s => new SavePlanSessionDto
            {
                DayOfWeek = s.DayOfWeek,
                Focus = s.Focus,
                Items = [.. source.Items.Where(i => i.SessionId == s.Id).OrderBy(i => i.OrderNo).Select(i => new SavePlanItemDto
                {
                    ExerciseId = i.ExerciseId, Sets = Math.Clamp(i.Sets ?? 3, 1, 20), Reps = i.RepsText ?? i.Reps?.ToString(), Notes = i.Notes,
                })],
            })],
        }, ct);
    }

    public async Task DeleteAsync(int memberId, int planId, CancellationToken ct)
    {
        var plan = await repo.OwnedPlanAsync(memberId, planId, ct) ?? throw new AuthException("Không tìm thấy lịch tập.");
        repo.RemovePlan(plan);
        await repo.SaveChangesAsync(ct);
    }

    private static int? FirstNumber(string? text) =>
        System.Text.RegularExpressions.Regex.Match(text ?? "", @"\d+") is { Success: true } m ? int.Parse(m.Value) : null;

    // Shared by member self-service plans and coach-made plans.
    internal static async Task ValidateContentAsync(ITrainingRepository repo, SavePlanDto dto, CancellationToken ct)
    {
        if (dto.Sessions.Select(s => s.DayOfWeek).Distinct().Count() != dto.Sessions.Count)
            throw new AuthException("Mỗi ngày trong tuần chỉ có một buổi tập.");
        var exerciseIds = dto.Sessions.SelectMany(s => s.Items).Select(i => i.ExerciseId).Distinct().ToList();
        if (await repo.CountExercisesAsync(exerciseIds, ct) != exerciseIds.Count)
            throw new AuthException("Có bài tập không tồn tại.");
    }

    internal static void ApplyContent(TrainingPlan plan, SavePlanDto dto)
    {
        plan.Items.Clear();
        plan.Sessions.Clear();
        plan.Title = dto.Title.Trim();
        plan.Goal = dto.Goal;
        plan.Description = dto.Description;
        plan.DaysPerWeek = dto.Sessions.Count;
        foreach (var s in dto.Sessions)
        {
            var session = new TrainingPlanSession { DayOfWeek = s.DayOfWeek, Focus = s.Focus.Trim() };
            plan.Sessions.Add(session);
            var order = 0;
            foreach (var i in s.Items)
                plan.Items.Add(new TrainingPlanItem
                {
                    ExerciseId = i.ExerciseId, Session = session, OrderNo = ++order, Sets = i.Sets,
                    RepsText = i.Reps, Reps = FirstNumber(i.Reps), Notes = i.Notes,
                });
        }
    }

    internal static PlanDto ToDto(TrainingPlan p) => new(p.Id, p.Title, p.Goal, p.Description, p.DaysPerWeek, p.IsActive, p.CreatedByAi, p.CreatedAt,
        p.Sessions.OrderBy(s => ((int)s.DayOfWeek + 6) % 7).Select(s => new PlanSessionDto(s.Id, s.DayOfWeek, s.Focus,
            p.Items.Where(i => i.SessionId == s.Id).OrderBy(i => i.OrderNo)
                .Select(i => new PlanItemDto(i.ExerciseId, i.Exercise.Name, i.Exercise.MuscleGroup, i.Exercise.ImageUrl, i.Sets, i.RepsText ?? i.Reps?.ToString(), i.Notes)).ToList())).ToList(),
        p.CoachId, p.Coach?.FullName, p.MemberId, p.Member?.FullName, p.ClassId, p.Class?.Name, p.StartDate, p.EndDate);
}
