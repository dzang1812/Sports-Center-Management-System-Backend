namespace SportsCenter.Repository.Entities;

// Class booking and schedule management.
public class Sport
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool HasClasses { get; set; }
    public bool HasPt { get; set; }
    public SportStatus Status { get; set; } = SportStatus.Active;
}

public class Room
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Capacity { get; set; }
    public RoomStatus Status { get; set; } = RoomStatus.Active;
    public bool IsActive { get; set; } = true; // compatibility with the initial schema
}

public class Class
{
    public int Id { get; set; }
    public int SportId { get; set; }
    public Sport Sport { get; set; } = null!;
    public int RoomId { get; set; }
    public Room Room { get; set; } = null!;
    public int? CoachId { get; set; }
    public User? Coach { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public Level Level { get; set; }
    public int Capacity { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public ClassStatus Status { get; set; } = ClassStatus.Draft;
    public int NotificationRevision { get; set; }
    public List<ClassSession> Sessions { get; set; } = [];
    public List<ClassEnrollment> Enrollments { get; set; } = [];
}

public class ClassSession
{
    public int Id { get; set; }
    public int ClassId { get; set; }
    public Class Class { get; set; } = null!;
    public int? CoachId { get; set; }
    public User? Coach { get; set; }
    public int RoomId { get; set; }
    public Room Room { get; set; } = null!;
    public string ClassName { get; set; } = "";
    public Level Level { get; set; }
    public int Capacity { get; set; }
    public DateOnly Date { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Scheduled;
    public string? Note { get; set; }
    public string? CancellationReason { get; set; }
    public DateTime? CancelledAt { get; set; }
    public int? CancelledByUserId { get; set; }
    public User? CancelledByUser { get; set; }
    public DateTime? AttendanceFinalizedAt { get; set; }
    public int? AttendanceFinalizedByUserId { get; set; }
    public User? AttendanceFinalizedByUser { get; set; }
    public int NotificationRevision { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ClassBooking
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public ClassSession Session { get; set; } = null!;
    public int MemberId { get; set; }
    public User Member { get; set; } = null!;
    public int PerformedByUserId { get; set; }
    public User PerformedByUser { get; set; } = null!;
    public int SubscriptionId { get; set; }
    public MemberSubscription Subscription { get; set; } = null!;
    public BookingStatus Status { get; set; } = BookingStatus.Confirmed;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CancelledAt { get; set; }
    public int? CancelledByUserId { get; set; }
    public User? CancelledByUser { get; set; }
    public string? CancellationReason { get; set; }
    public bool IsLateCancellation { get; set; }
}

public class CoachAvailability
{
    public int Id { get; set; }
    public int CoachId { get; set; }
    public User Coach { get; set; } = null!;
    public int SportId { get; set; }
    public Sport Sport { get; set; } = null!;
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class PtBooking
{
    public int Id { get; set; }
    public int MemberId { get; set; }
    public User Member { get; set; } = null!;
    public int CoachId { get; set; }
    public User Coach { get; set; } = null!;
    public int SportId { get; set; }
    public Sport Sport { get; set; } = null!;
    public int? RoomId { get; set; }
    public Room? Room { get; set; }
    public int SubscriptionId { get; set; }
    public MemberSubscription Subscription { get; set; } = null!;
    public int PerformedByUserId { get; set; }
    public User PerformedByUser { get; set; } = null!;
    public DateOnly Date { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public BookingStatus Status { get; set; } = BookingStatus.Confirmed;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CancelledAt { get; set; }
    public int? CancelledByUserId { get; set; }
    public User? CancelledByUser { get; set; }
    public string? CancellationReason { get; set; }
    public bool CreditRefunded { get; set; }
    public DateTime? CreditRefundedAt { get; set; }
    public bool IsLateCancellation { get; set; }
    public DateTime? AttendanceFinalizedAt { get; set; }
    public int? AttendanceFinalizedByUserId { get; set; }
    public User? AttendanceFinalizedByUser { get; set; }
}

public class ClassEnrollment // legacy class-level enrollment retained for compatibility
{
    public int Id { get; set; }
    public int ClassId { get; set; }
    public Class Class { get; set; } = null!;
    public int MemberId { get; set; }
    public User Member { get; set; } = null!;
    public EnrollmentStatus Status { get; set; } = EnrollmentStatus.Enrolled;
    public DateTime EnrolledAt { get; set; } = DateTime.UtcNow;
    public DateTime? CancelledAt { get; set; }
}

public class CheckIn
{
    public int Id { get; set; }
    public int MemberId { get; set; }
    public User Member { get; set; } = null!;
    public int? SubscriptionId { get; set; }
    public MemberSubscription? Subscription { get; set; }
    public int CheckedInById { get; set; }
    public User CheckedInBy { get; set; } = null!;
    public DateTime CheckedInAt { get; set; } = DateTime.UtcNow;
    public string CheckInSource { get; set; } = "ReceptionDesk";
    public int? CheckedOutById { get; set; }
    public User? CheckedOutBy { get; set; }
    public DateTime? CheckedOutAt { get; set; }
    public string? CheckOutSource { get; set; }
}

// Exactly one target is set: ClassSession or PtBooking.
public class Attendance
{
    public int Id { get; set; }
    public int? SessionId { get; set; }
    public ClassSession? Session { get; set; }
    public int? PtBookingId { get; set; }
    public PtBooking? PtBooking { get; set; }
    public int MemberId { get; set; }
    public User Member { get; set; } = null!;
    public AttendanceStatus Status { get; set; } = AttendanceStatus.Absent;
    public int MarkedById { get; set; }
    public User MarkedBy { get; set; } = null!;
    public DateTime MarkedAt { get; set; } = DateTime.UtcNow;
}
