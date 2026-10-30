namespace SportsCenter.Services.Ai;

public class ImageAnalyzeResponseDto
{
    public string Mode { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> DetectedItems { get; set; } = [];
    public List<string> BodyObservations { get; set; } = [];
    public List<string> Muscles { get; set; } = [];
    public List<string> PriorityMuscles { get; set; } = [];
    public List<string> SuggestedExercises { get; set; } = [];
    public List<string> FormFeedback { get; set; } = [];
    public List<string> TrainingAdvice { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public class VideoFormAnalyzeResponseDto
{
    public string Mode { get; set; } = "video_form_check";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string TargetExercise { get; set; } = "auto_detect";
    public string DetectedExercise { get; set; } = "";
    public string Confidence { get; set; } = "medium";
    public bool IsFormAcceptable { get; set; }
    public string RiskLevel { get; set; } = "medium";
    public string OverallVerdict { get; set; } = "";
    public string MovementSummary { get; set; } = "";
    public List<string> MajorIssues { get; set; } = [];
    public List<string> MinorIssues { get; set; } = [];
    public List<string> CorrectPoints { get; set; } = [];
    public List<string> FrameObservations { get; set; } = [];
    public List<string> CorrectiveCues { get; set; } = [];
    public List<string> SuggestedFixes { get; set; } = [];
    public List<string> Muscles { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

/// <summary>Numbers computed in C#; the AI only writes the narrative around them and must not recompute.</summary>
public class WorkoutEvaluationMetricsDto
{
    public string Focus { get; set; } = "";
    public int Score { get; set; }
    public string Grade { get; set; } = "";
    public int DurationMinutes { get; set; }
    public int ExerciseCount { get; set; }
    public int TotalSets { get; set; }
    public int TotalReps { get; set; }
    public double TotalVolumeKg { get; set; }
    public int EstimatedCalories { get; set; }
    public string ComparisonSummary { get; set; } = "";
    public List<string> Highlights { get; set; } = [];
    public List<string> Improvements { get; set; } = [];
    public List<string> MuscleRecoverySummary { get; set; } = [];
    public int ProteinGrams { get; set; }
    public double WaterLiters { get; set; }
    public int SessionsLast14Days { get; set; }
    public string? NextPlannedSession { get; set; }
}

public class WorkoutEvaluationNarrativeDto
{
    public string NarrativeSummary { get; set; } = "";
    public string MealSuggestion { get; set; } = "";
    public string SuggestedNextWorkout { get; set; } = "";
    public string MotivationalMessage { get; set; } = "";
}

public class WorkoutEvaluationReport
{
    public int SessionId { get; set; }
    public int Score { get; set; }
    public string Grade { get; set; } = "";
    public WorkoutEvaluationSummary Summary { get; set; } = new();
    public WorkoutSessionComparison Comparison { get; set; } = new();
    public List<string> Highlights { get; set; } = [];
    public List<string> Improvements { get; set; } = [];
    public List<MuscleRecoveryStatus> Recovery { get; set; } = [];
    public NutritionRecommendation Nutrition { get; set; } = new();
    public string NarrativeSummary { get; set; } = "";
    public string SuggestedNextWorkout { get; set; } = "";
    public string MotivationalMessage { get; set; } = "";
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}

public class WorkoutEvaluationSummary
{
    public int DurationMinutes { get; set; }
    public int ExerciseCount { get; set; }
    public int TotalSets { get; set; }
    public int TotalReps { get; set; }
    public double TotalVolumeKg { get; set; }
    public int EstimatedCalories { get; set; }
}

/// <summary>Compared with the member's most recent earlier session of the same class.</summary>
public class WorkoutSessionComparison
{
    public bool HasPrevious { get; set; }
    public DateOnly? PreviousDate { get; set; }
    public double PreviousVolumeKg { get; set; }
    public double VolumeDeltaPercent { get; set; }
    public int PreviousSets { get; set; }
    public int SetsDelta { get; set; }
    public int RepsDelta { get; set; }
    public int CaloriesDelta { get; set; }
}

public class MuscleRecoveryStatus
{
    public string MuscleCategory { get; set; } = "";
    public string Status { get; set; } = "";
    public int RecoveryHours { get; set; }
    public DateTime ReadyAt { get; set; }
}

public class NutritionRecommendation
{
    public int ProteinGrams { get; set; }
    public double WaterLiters { get; set; }
    public string MealSuggestion { get; set; } = "";
}
