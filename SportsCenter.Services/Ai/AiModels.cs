namespace SportsCenter.Services.Ai;

public enum AiFeature { Chat, GenerateWorkoutPlan, AnalyzeImage, AnalyzeFormVideo, EvaluateWorkout }

public class AiUsageCheckResult
{
    public bool Allowed { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }
    public int? Remaining { get; set; } // anonymous chat only: questions left today for this visitor

    public static AiUsageCheckResult Allow() => new() { Allowed = true };
    public static AiUsageCheckResult Deny(string code, string message) => new() { Allowed = false, Code = code, Message = message };
}

public class AiSuggestionDto
{
    public string Action { get; set; } = "";
    public string PlanId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string ExerciseId { get; set; } = "";
    public string PlanName { get; set; } = "";
    public string Goal { get; set; } = "";
    public string PlanDescription { get; set; } = "";
    public int DaysPerWeek { get; set; }
    public string DayOfWeek { get; set; } = "";
    public string Focus { get; set; } = "";
    public int Sets { get; set; }
    public string Reps { get; set; } = "";
    public string Notes { get; set; } = "";
}

public class ChatRequestDto
{
    public string Message { get; set; } = "";
}

public class PublicChatTurnDto
{
    public string Role { get; set; } = ""; // user | assistant
    public string Content { get; set; } = "";
}

// Visitors are not logged in: the client keeps the short conversation and sends the last few turns back.
public class PublicChatRequestDto
{
    public string Message { get; set; } = "";
    public List<PublicChatTurnDto> History { get; set; } = [];
}

public class PublicChatResponseDto
{
    public string Response { get; set; } = "";
    public int Remaining { get; set; }
}

public class ChatResponseDto
{
    public string Response { get; set; } = "";
    public List<AiSuggestionDto> Suggestions { get; set; } = [];
}

public class ChatHistoryDto
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class ApplySuggestionsRequestDto
{
    public List<AiSuggestionDto> Suggestions { get; set; } = [];
}

public class GenerateWorkoutPlanRequestDto
{
    public string Goal { get; set; } = "";
    public string ExperienceLevel { get; set; } = "";
    public int? DaysPerWeek { get; set; }
    public List<string> TrainingDays { get; set; } = [];
    public string Intensity { get; set; } = "";
    public string TrainingCondition { get; set; } = "";
    public string HealthIssues { get; set; } = "";
}

public class AiUsageQuotaDto
{
    public int Used { get; set; }
    public int Limit { get; set; }
}

public class AiUsageSnapshotDto
{
    public bool HasActiveSubscription { get; set; }
    public AiUsageQuotaDto Chat { get; set; } = new();
    public AiUsageQuotaDto Generate { get; set; } = new();
    public AiUsageQuotaDto Analyze { get; set; } = new();
    public AiUsageQuotaDto Evaluate { get; set; } = new();
    public bool BudgetExhausted { get; set; }
}
