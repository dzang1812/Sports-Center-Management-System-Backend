namespace SportsCenter.Repository.Entities;

// Flow 5 + 6: AI usage control. Chat history and suggestions live in Common.cs.

// Total OpenAI spend per month: a hard circuit breaker independent of the per-user quota.
public class AiMonthlyBudget
{
    public string Period { get; set; } = ""; // PK, "yyyy-MM"
    public decimal SpentUsd { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

// Per-user, per-day counters (composite PK UserId + Date).
public class AiDailyQuota
{
    public int UserId { get; set; }
    public string Date { get; set; } = ""; // "yyyy-MM-dd"
    public int ChatCount { get; set; }
    public int GenerateCount { get; set; }
    public int AnalyzeCount { get; set; }
    public int EvaluateCount { get; set; }
}

// One row per OpenAI call, for cost reconciliation.
public class AiUsageLog
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Feature { get; set; } = "";
    public string Model { get; set; } = "";
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public decimal CostUsd { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// What a member asked a paid AI feature and what came back (manager-facing usage stats).
public class AiFeatureUsageLog
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Feature { get; set; } = ""; // EquipmentInfo, BodyCheck, FormCheckVideo, GenerateWorkoutPlan
    public bool HasActiveSubscription { get; set; }
    public string ResponseSummary { get; set; } = "";
    public string? RequestSnapshot { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Cached AI report for one member + class session, so reopening it never costs another OpenAI call.
public class AiWorkoutEvaluation
{
    public int Id { get; set; }
    public int MemberId { get; set; }
    public User Member { get; set; } = null!;
    public int SessionId { get; set; }
    public ClassSession Session { get; set; } = null!;
    public string Report { get; set; } = "{}"; // jsonb
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
