namespace SportsCenter.Repository.Entities;

// Flow 4: training plans, results, evaluations.
public class Exercise
{
    public int Id { get; set; }
    public int? SportId { get; set; }
    public Sport? Sport { get; set; }
    public string Name { get; set; } = "";
    public string? MuscleGroup { get; set; }
    public Level Level { get; set; }
    public string? Description { get; set; }
    public string? Equipment { get; set; }
    public int? DefaultSets { get; set; }
    public string? DefaultReps { get; set; } // free-form such as "8-12"
    public int? RestSeconds { get; set; }
    public string? ImageUrl { get; set; } // Cloudinary
    public string? VideoUrl { get; set; }
}

// Exactly one of MemberId / ClassId is set (DB check constraint) = individual plan vs whole-class plan.
public class TrainingPlan
{
    public int Id { get; set; }
    public int? CoachId { get; set; } // null = self-service plan created by the member (e.g. via AI)
    public User? Coach { get; set; }
    public int? MemberId { get; set; }
    public User? Member { get; set; }
    public int? ClassId { get; set; }
    public Class? Class { get; set; }
    public string Title { get; set; } = "";
    public string? Goal { get; set; }
    public string? Description { get; set; }
    public int DaysPerWeek { get; set; }
    public bool IsActive { get; set; } = true; // the AI apply flow keeps one active plan per member
    public bool CreatedByAi { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<TrainingPlanItem> Items { get; set; } = [];
    public List<TrainingPlanSession> Sessions { get; set; } = [];
}

// One training day inside a plan (AI plans group exercises by weekday).
public class TrainingPlanSession
{
    public int Id { get; set; }
    public int PlanId { get; set; }
    public TrainingPlan Plan { get; set; } = null!;
    public DayOfWeek DayOfWeek { get; set; }
    public string Focus { get; set; } = "";
}

public class TrainingPlanItem
{
    public int Id { get; set; }
    public int PlanId { get; set; }
    public TrainingPlan Plan { get; set; } = null!;
    public int ExerciseId { get; set; }
    public Exercise Exercise { get; set; } = null!;
    public int OrderNo { get; set; }
    public int? Sets { get; set; }
    public int? Reps { get; set; }
    public string? RepsText { get; set; } // free-form target such as "10-12" (AI plans); Reps keeps the first number
    public int? DurationMin { get; set; }
    public string? Notes { get; set; }
    public int? SessionId { get; set; } // TrainingPlanSession; null for coach-made flat plans
    public TrainingPlanSession? Session { get; set; }
}

public class TrainingResult // what the member actually did, recorded by the coach after a session
{
    public int Id { get; set; }
    public int MemberId { get; set; }
    public User Member { get; set; } = null!;
    public int RecordedById { get; set; }
    public User RecordedBy { get; set; } = null!;
    public int? SessionId { get; set; } // class session, or
    public ClassSession? Session { get; set; }
    public int? PtBookingId { get; set; } // PT session
    public PtBooking? PtBooking { get; set; }
    public int? ExerciseId { get; set; }
    public Exercise? Exercise { get; set; }
    public int? Sets { get; set; }
    public int? Reps { get; set; }
    public decimal? WeightKg { get; set; }
    public int? DurationMin { get; set; }
    public string? Notes { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}

public class ProgressEvaluation
{
    public int Id { get; set; }
    public int MemberId { get; set; }
    public User Member { get; set; } = null!;
    public int CoachId { get; set; }
    public User Coach { get; set; } = null!;
    public int? Score { get; set; } // 1-10
    public string Comment { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
