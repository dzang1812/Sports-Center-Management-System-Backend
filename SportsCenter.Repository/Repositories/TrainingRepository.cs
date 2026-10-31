using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

public interface ITrainingRepository
{
    Task<List<Exercise>> ExercisesAsync(string? muscleGroup, string? search, CancellationToken ct);
    Task<List<string>> MuscleGroupsAsync(CancellationToken ct);
    Task<List<Exercise>> TrackedExercisesAsync(CancellationToken ct);
    void AddExercise(Exercise exercise);
    Task<int> CountExercisesAsync(IEnumerable<int> ids, CancellationToken ct);
    Task<List<TrainingPlan>> PlansAsync(int memberId, CancellationToken ct);
    Task<TrainingPlan?> OwnedPlanAsync(int memberId, int planId, CancellationToken ct);
    Task DeactivatePlansAsync(int memberId, CancellationToken ct);
    void AddPlan(TrainingPlan plan);
    void RemovePlan(TrainingPlan plan);
    Task SaveChangesAsync(CancellationToken ct);
}

public class TrainingRepository(AppDbContext db) : ITrainingRepository
{
    public Task<List<Exercise>> ExercisesAsync(string? muscleGroup, string? search, CancellationToken ct)
    {
        var q = db.Exercises.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(muscleGroup)) q = q.Where(x => x.MuscleGroup == muscleGroup);
        if (!string.IsNullOrWhiteSpace(search)) q = q.Where(x => EF.Functions.ILike(x.Name, $"%{search.Trim()}%"));
        return q.OrderBy(x => x.MuscleGroup).ThenBy(x => x.Name).ToListAsync(ct);
    }

    public Task<List<string>> MuscleGroupsAsync(CancellationToken ct) =>
        db.Exercises.Where(x => x.MuscleGroup != null).Select(x => x.MuscleGroup!).Distinct().OrderBy(x => x).ToListAsync(ct);

    public Task<List<Exercise>> TrackedExercisesAsync(CancellationToken ct) => db.Exercises.ToListAsync(ct);

    public void AddExercise(Exercise exercise) => db.Exercises.Add(exercise);

    public Task<int> CountExercisesAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToList();
        return db.Exercises.CountAsync(x => distinct.Contains(x.Id), ct);
    }

    public Task<List<TrainingPlan>> PlansAsync(int memberId, CancellationToken ct) =>
        db.TrainingPlans.AsNoTracking().AsSplitQuery()
            .Include(x => x.Sessions).Include(x => x.Items).ThenInclude(i => i.Exercise)
            .Where(x => x.MemberId == memberId)
            .OrderByDescending(x => x.IsActive).ThenByDescending(x => x.Id).ToListAsync(ct);

    public Task<TrainingPlan?> OwnedPlanAsync(int memberId, int planId, CancellationToken ct) =>
        db.TrainingPlans.AsSplitQuery()
            .Include(x => x.Sessions).Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == planId && x.MemberId == memberId, ct);

    public Task DeactivatePlansAsync(int memberId, CancellationToken ct) =>
        db.TrainingPlans.Where(x => x.MemberId == memberId && x.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), ct);

    public void AddPlan(TrainingPlan plan) => db.TrainingPlans.Add(plan);

    public void RemovePlan(TrainingPlan plan) => db.TrainingPlans.Remove(plan);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
