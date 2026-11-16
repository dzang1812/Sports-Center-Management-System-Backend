using System.Text.Json;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;

namespace SportsCenter.Services.Ai;

public interface IWorkoutEvaluationService
{
    Task<(AiUsageCheckResult Check, WorkoutEvaluationReport? Report)> EvaluateAsync(int memberId, int sessionId, CancellationToken ct = default);
}

/// <summary>
/// Scores one class session from the coach-recorded TrainingResults. Every number (score, grade, volume,
/// recovery, nutrition) is computed here deterministically; the AI is called once, only to write the
/// narrative, and the whole report is cached so reopening it costs nothing.
/// </summary>
public class WorkoutEvaluationService(IAiRepository repo, IAiUsageService usage, IAiService ai) : IWorkoutEvaluationService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    // Hours before the same muscle group is fully recovered; unknown groups use 36.
    private static readonly Dictionary<string, int> BaseRecoveryHours = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Legs"] = 72, ["Back"] = 72, ["Chest"] = 48, ["Shoulders"] = 48, ["Arms"] = 36, ["Abs"] = 24, ["Core"] = 24,
    };

    private const double ResistanceTrainingMet = 6.0; // kcal = MET * weight(kg) * hours

    public async Task<(AiUsageCheckResult Check, WorkoutEvaluationReport? Report)> EvaluateAsync(int memberId, int sessionId, CancellationToken ct = default)
    {
        var session = await repo.GetSessionAsync(sessionId, ct);
        var results = session is null ? [] : await repo.ResultsAsync(memberId, sessionId, ct);
        if (session is null || results.Count == 0)
            return (AiUsageCheckResult.Deny("NOT_FOUND", "Chưa có kết quả tập luyện nào được ghi nhận cho buổi này."), null);

        var cached = await repo.GetEvaluationAsync(memberId, sessionId, ct);
        if (cached is not null)
            return (AiUsageCheckResult.Allow(), JsonSerializer.Deserialize<WorkoutEvaluationReport>(cached.Report, Json));

        var check = await usage.CheckAndReserveAsync(memberId, AiFeature.EvaluateWorkout, ct);
        if (!check.Allowed) return (check, null);

        var member = await repo.GetMemberAsync(memberId, ct);
        var weightKg = member?.MemberProfile?.WeightKg is > 0 ? (double)member.MemberProfile.WeightKg.Value : 70;

        var past = await repo.PastResultsAsync(memberId, sessionId, 300, ct);
        var bySession = past.GroupBy(r => r.SessionId!.Value)
            .Select(g => (Session: g.First().Session!, Results: g.ToList())).ToList();
        var sameClass = bySession.Where(s => s.Session.ClassId == session.ClassId).ToList();

        var duration = Math.Max(0, (int)(session.EndTime.ToTimeSpan() - session.StartTime.ToTimeSpan()).TotalMinutes);
        var summary = Summarize(results, duration, weightKg);

        var completion = 1.0; // every recorded result counts as done; skipped planned exercises are reported below
        var baselineVolume = sameClass.Take(3).Select(s => Volume(s.Results)).DefaultIfEmpty(0).Average();
        var volumeRatio = baselineVolume > 0 ? summary.TotalVolumeKg / baselineVolume : 1.0;
        var varietyRatio = Math.Min(1.0, results.Select(r => r.Exercise?.MuscleGroup).Where(g => g != null).Distinct().Count() / 3.0);
        var loadRatio = results.Count(r => r.WeightKg is > 0) / (double)results.Count;
        var score = (int)Math.Round(Math.Clamp(completion * 40 + Math.Min(1.1, volumeRatio) * 30 + varietyRatio * 15 + loadRatio * 15, 0, 100));
        var grade = score switch { >= 95 => "A+", >= 88 => "A", >= 80 => "B+", >= 72 => "B", >= 64 => "C+", >= 55 => "C", _ => "D" };

        var plan = await repo.GetActivePlanAsync(memberId, ct);
        var highlights = BuildHighlights(results, past, volumeRatio, bySession.Count > 0);
        var improvements = BuildImprovements(results, plan, session, varietyRatio, loadRatio);
        var recovery = BuildRecovery(results, session);
        var nutrition = new NutritionRecommendation
        {
            ProteinGrams = (int)Math.Round(weightKg * 1.8),
            WaterLiters = Math.Round(weightKg * 0.033 + duration / 15.0 * 0.2, 1),
            MealSuggestion = "Ưu tiên đạm nạc và tinh bột phức hợp trong bữa ăn sau tập.",
        };
        var comparison = Compare(sameClass.FirstOrDefault(), summary, results, weightKg);
        var recent = bySession.Where(s => s.Session.Date >= session.Date.AddDays(-14)).Count() + 1;
        var next = NextPlanned(plan, session.Date.DayOfWeek);

        var narrative = await ai.EvaluateWorkoutAsync(memberId, new WorkoutEvaluationMetricsDto
        {
            Focus = session.ClassName, Score = score, Grade = grade,
            DurationMinutes = summary.DurationMinutes, ExerciseCount = summary.ExerciseCount, TotalSets = summary.TotalSets,
            TotalReps = summary.TotalReps, TotalVolumeKg = summary.TotalVolumeKg, EstimatedCalories = summary.EstimatedCalories,
            ComparisonSummary = ComparisonText(comparison), Highlights = highlights, Improvements = improvements,
            MuscleRecoverySummary = recovery.Select(r => $"{r.MuscleCategory}: {r.Status} (~{r.RecoveryHours}h)").ToList(),
            ProteinGrams = nutrition.ProteinGrams, WaterLiters = nutrition.WaterLiters,
            SessionsLast14Days = recent, NextPlannedSession = next,
        }, ct);
        if (!string.IsNullOrWhiteSpace(narrative.MealSuggestion)) nutrition.MealSuggestion = narrative.MealSuggestion;

        var report = new WorkoutEvaluationReport
        {
            SessionId = sessionId, Score = score, Grade = grade, Summary = summary, Comparison = comparison,
            Highlights = highlights, Improvements = improvements, Recovery = recovery, Nutrition = nutrition,
            NarrativeSummary = narrative.NarrativeSummary, SuggestedNextWorkout = narrative.SuggestedNextWorkout,
            MotivationalMessage = narrative.MotivationalMessage,
        };
        await repo.AddEvaluationAsync(new AiWorkoutEvaluation
        {
            MemberId = memberId, SessionId = sessionId, Report = JsonSerializer.Serialize(report, Json),
        }, ct);
        return (AiUsageCheckResult.Allow(), report);
    }

    private static double Volume(List<TrainingResult> rows) =>
        rows.Sum(r => (r.Sets ?? 1) * (r.Reps ?? 0) * (double)(r.WeightKg ?? 0));

    private static WorkoutEvaluationSummary Summarize(List<TrainingResult> rows, int duration, double weightKg) => new()
    {
        DurationMinutes = duration,
        ExerciseCount = rows.Where(r => r.ExerciseId != null).Select(r => r.ExerciseId).Distinct().Count(),
        TotalSets = rows.Sum(r => r.Sets ?? 0),
        TotalReps = rows.Sum(r => (r.Sets ?? 1) * (r.Reps ?? 0)),
        TotalVolumeKg = Math.Round(Volume(rows), 1),
        EstimatedCalories = (int)Math.Round(ResistanceTrainingMet * weightKg * (duration / 60.0)),
    };

    private static WorkoutSessionComparison Compare(
        (ClassSession Session, List<TrainingResult> Results) previous, WorkoutEvaluationSummary now, List<TrainingResult> results, double weightKg)
    {
        if (previous.Session is null) return new WorkoutSessionComparison { HasPrevious = false };
        var before = Summarize(previous.Results, (int)(previous.Session.EndTime.ToTimeSpan() - previous.Session.StartTime.ToTimeSpan()).TotalMinutes, weightKg);
        return new WorkoutSessionComparison
        {
            HasPrevious = true,
            PreviousDate = previous.Session.Date,
            PreviousVolumeKg = before.TotalVolumeKg,
            VolumeDeltaPercent = before.TotalVolumeKg > 0 ? Math.Round((now.TotalVolumeKg - before.TotalVolumeKg) / before.TotalVolumeKg * 100, 1) : 0,
            PreviousSets = before.TotalSets,
            SetsDelta = now.TotalSets - before.TotalSets,
            RepsDelta = now.TotalReps - before.TotalReps,
            CaloriesDelta = now.EstimatedCalories - before.EstimatedCalories,
        };
    }

    private static string ComparisonText(WorkoutSessionComparison c)
    {
        if (!c.HasPrevious) return "Đây là lần đầu tiên tập buổi này, chưa có dữ liệu để so sánh.";
        var direction = c.VolumeDeltaPercent > 0 ? "tăng" : c.VolumeDeltaPercent < 0 ? "giảm" : "giữ nguyên";
        string Signed(double v) => (v >= 0 ? "+" : "") + v;
        return $"So với lần tập gần nhất ({c.PreviousDate:dd/MM}): khối lượng {direction} {Math.Abs(c.VolumeDeltaPercent)}%, " +
               $"sets {Signed(c.SetsDelta)}, reps {Signed(c.RepsDelta)}, calo {Signed(c.CaloriesDelta)}.";
    }

    private static List<string> BuildHighlights(List<TrainingResult> results, List<TrainingResult> past, double volumeRatio, bool hasHistory)
    {
        var list = new List<string>();
        if (volumeRatio > 1.02 && hasHistory) list.Add("Tăng khối lượng tập luyện so với các buổi gần đây");
        foreach (var g in results.Where(r => r.ExerciseId != null && r.WeightKg is > 0).GroupBy(r => r.ExerciseId!.Value))
        {
            var best = g.OrderByDescending(r => r.WeightKg).First();
            var previousBest = past.Where(p => p.ExerciseId == g.Key).Select(p => p.WeightKg ?? 0).DefaultIfEmpty(0).Max();
            if (previousBest > 0 && best.WeightKg > previousBest)
                list.Add($"PR mới: {best.Exercise?.Name} {best.WeightKg}kg x {best.Reps}");
        }
        return list;
    }

    private static List<string> BuildImprovements(
        List<TrainingResult> results, TrainingPlan? plan, ClassSession session, double varietyRatio, double loadRatio)
    {
        var list = new List<string>();
        var done = results.Select(r => r.ExerciseId).ToHashSet();
        var planned = plan?.Sessions.FirstOrDefault(s => s.DayOfWeek == session.Date.DayOfWeek);
        var skipped = plan?.Items.Where(i => i.SessionId == planned?.Id && !done.Contains(i.ExerciseId)).Select(i => i.Exercise.Name).ToList();
        if (skipped is { Count: > 0 }) list.Add($"Đã bỏ qua: {string.Join(", ", skipped)}");
        if (loadRatio < 0.5) list.Add("Nhiều bài chưa ghi nhận mức tạ - hãy ghi lại để theo dõi tiến bộ chính xác hơn");
        if (varietyRatio < 0.5) list.Add("Buổi tập tập trung vào ít nhóm cơ - cân nhắc đa dạng bài tập hơn");
        return list;
    }

    private static List<MuscleRecoveryStatus> BuildRecovery(List<TrainingResult> results, ClassSession session)
    {
        var finishedAt = session.Date.ToDateTime(session.EndTime);
        return results.Where(r => !string.IsNullOrWhiteSpace(r.Exercise?.MuscleGroup))
            .GroupBy(r => r.Exercise!.MuscleGroup!, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                // More work on a muscle group stretches its recovery by up to 2x.
                var load = g.Sum(r => (r.Sets ?? 1) * (r.Reps ?? 1));
                var hours = (int)Math.Round(BaseRecoveryHours.GetValueOrDefault(g.Key, 36) * (1 + Math.Min(1.0, load / 60.0)));
                return new MuscleRecoveryStatus
                {
                    MuscleCategory = g.Key, RecoveryHours = hours, ReadyAt = finishedAt.AddHours(hours),
                    Status = hours >= 48 ? "Cần nghỉ" : hours >= 24 ? "Đang hồi phục" : "Sẵn sàng sớm",
                };
            }).OrderByDescending(x => x.RecoveryHours).ToList();
    }

    private static string? NextPlanned(TrainingPlan? plan, DayOfWeek today)
    {
        if (plan is null || plan.Sessions.Count == 0) return null;
        var ordered = plan.Sessions.OrderBy(s => ((int)s.DayOfWeek - (int)today + 7) % 7 == 0 ? 7 : ((int)s.DayOfWeek - (int)today + 7) % 7).First();
        return $"{ordered.Focus} ({ordered.DayOfWeek})";
    }
}
