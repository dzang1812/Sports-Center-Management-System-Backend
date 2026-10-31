namespace SportsCenter.Repository.Entities;

// One row per recipient. Coach "bài tập về nhà" = Type Homework (deadline goes in Body).
public class Notification
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public int? SenderId { get; set; }
    public User? Sender { get; set; }
    public NotificationType Type { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public bool IsRead { get; set; }
    public string? DeduplicationKey { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class SupportRequest
{
    public int Id { get; set; }
    public int MemberId { get; set; }
    public User Member { get; set; } = null!;
    public int? HandledById { get; set; }
    public User? HandledBy { get; set; }
    public string Subject { get; set; } = "";
    public string Description { get; set; } = "";
    public SupportStatus Status { get; set; } = SupportStatus.Open;
    public SupportCategory Category { get; set; } = SupportCategory.Other;
    public int? CreatedById { get; set; } // staff who logged it at the desk; null = the member submitted it
    public User? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
    public List<SupportMessage> Messages { get; set; } = [];
}

// Conversation on a request. IsInternal = staff-only note, never shown to the member.
public class SupportMessage
{
    public int Id { get; set; }
    public int RequestId { get; set; }
    public SupportRequest Request { get; set; } = null!;
    public int AuthorId { get; set; }
    public User Author { get; set; } = null!;
    public string Body { get; set; } = "";
    public bool IsInternal { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AuditLog // important actions only (manager view)
{
    public int Id { get; set; }
    public int? UserId { get; set; }
    public User? User { get; set; }
    public string Action { get; set; } = "";
    public string EntityName { get; set; } = "";
    public string? EntityId { get; set; }
    public string? Details { get; set; } // jsonb
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Flow 5 + 6: AI.
public class AiSuggestion // workout recommendation
{
    public int Id { get; set; }
    public int MemberId { get; set; }
    public User Member { get; set; } = null!;
    public int RequestedById { get; set; } // coach (or member)
    public User RequestedBy { get; set; } = null!;
    public string Input { get; set; } = "{}";  // jsonb snapshot: goal, level, history
    public string Output { get; set; } = "{}"; // jsonb: suggested exercises
    public AiSuggestionStatus Status { get; set; } = AiSuggestionStatus.Suggested;
    public int? TrainingPlanId { get; set; } // set when the coach turns it into a plan
    public TrainingPlan? TrainingPlan { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AiChatMessage // AI assistant history, one stream per user
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public ChatRole Role { get; set; }
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
