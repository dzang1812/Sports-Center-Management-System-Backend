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
public record PlanDto(int Id, string Title, string? Goal, string? Description, int DaysPerWeek, bool IsActive, bool CreatedByAi,
    DateTime CreatedAt, List<PlanSessionDto> Sessions);

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
    Task DeleteAsync(int memberId, int planId, CancellationToken ct);
}

public class TrainingService(ITrainingRepository repo, IAppClock clock) : ITrainingService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed record SeedRow(string Name, string? Equipment, string Level, int? Sets, string? Reps, int? Rest, string? MuscleGroup, string? Description);

    public async Task<List<ExerciseDto>> ExercisesAsync(string? muscleGroup, string? search, CancellationToken ct) =>
        (await repo.ExercisesAsync(muscleGroup, search, ct)).Select(x => new ExerciseDto(x.Id, x.Name, x.MuscleGroup,
            x.Level.ToString(), x.Description, x.Equipment, x.DefaultSets, x.DefaultReps, x.RestSeconds, x.ImageUrl, x.VideoUrl)).ToList();

    public Task<List<string>> MuscleGroupsAsync(CancellationToken ct) => repo.MuscleGroupsAsync(ct);

    /// <summary>Adds the bundled exercises that are not in the table yet (matched by name); safe to run repeatedly.</summary>
    public async Task<int> SeedExercisesAsync(string seedFile, CancellationToken ct)
    {
        var rows = JsonSerializer.Deserialize<List<SeedRow>>(await File.ReadAllTextAsync(seedFile, ct), Json) ?? [];
        var known = (await repo.TrackedExercisesAsync(ct)).Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var r in rows.Where(r => known.Add(r.Name)))
        {
            repo.AddExercise(new Exercise
            {
                Name = r.Name, Equipment = r.Equipment, MuscleGroup = r.MuscleGroup, Description = r.Description,
                Level = Enum.TryParse<Level>(r.Level, true, out var lv) ? lv : Level.Beginner,
                DefaultSets = r.Sets, DefaultReps = r.Reps, RestSeconds = r.Rest,
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
        if (dto.Sessions.Select(s => s.DayOfWeek).Distinct().Count() != dto.Sessions.Count)
            throw new AuthException("Mỗi ngày trong tuần chỉ có một buổi tập.");
        var exerciseIds = dto.Sessions.SelectMany(s => s.Items).Select(i => i.ExerciseId).Distinct().ToList();
        if (await repo.CountExercisesAsync(exerciseIds, ct) != exerciseIds.Count)
            throw new AuthException("Có bài tập không tồn tại.");

        TrainingPlan plan;
        if (planId is null)
        {
            plan = new TrainingPlan { MemberId = memberId, StartDate = DateOnly.FromDateTime(clock.LocalNow) };
            repo.AddPlan(plan);
        }
        else
        {
            plan = await repo.OwnedPlanAsync(memberId, planId.Value, ct) ?? throw new AuthException("Không tìm thấy lịch tập.");
            plan.Items.Clear();
            plan.Sessions.Clear();
        }

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

        if (dto.Activate) { await repo.DeactivatePlansAsync(memberId, ct); plan.IsActive = true; }
        else if (planId is null) plan.IsActive = false;
        await repo.SaveChangesAsync(ct);
        return ToDto((await repo.PlansAsync(memberId, ct)).First(p => p.Id == plan.Id));
    }

    public async Task ActivateAsync(int memberId, int planId, CancellationToken ct)
    {
        var plan = await repo.OwnedPlanAsync(memberId, planId, ct) ?? throw new AuthException("Không tìm thấy lịch tập.");
        await repo.DeactivatePlansAsync(memberId, ct);
        plan.IsActive = true;
        await repo.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int memberId, int planId, CancellationToken ct)
    {
        var plan = await repo.OwnedPlanAsync(memberId, planId, ct) ?? throw new AuthException("Không tìm thấy lịch tập.");
        repo.RemovePlan(plan);
        await repo.SaveChangesAsync(ct);
    }

    private static int? FirstNumber(string? text) =>
        System.Text.RegularExpressions.Regex.Match(text ?? "", @"\d+") is { Success: true } m ? int.Parse(m.Value) : null;

    private static PlanDto ToDto(TrainingPlan p) => new(p.Id, p.Title, p.Goal, p.Description, p.DaysPerWeek, p.IsActive, p.CreatedByAi, p.CreatedAt,
        p.Sessions.OrderBy(s => ((int)s.DayOfWeek + 6) % 7).Select(s => new PlanSessionDto(s.Id, s.DayOfWeek, s.Focus,
            p.Items.Where(i => i.SessionId == s.Id).OrderBy(i => i.OrderNo)
                .Select(i => new PlanItemDto(i.ExerciseId, i.Exercise.Name, i.Exercise.MuscleGroup, i.Exercise.ImageUrl, i.Sets, i.RepsText ?? i.Reps?.ToString(), i.Notes)).ToList())).ToList());
}
