namespace SportsCenter.Repository.Entities;

// Flow 2: class booking & schedule.
public class Sport
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
}

public class Room
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Capacity { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Class
{
    public int Id { get; set; }
    public int SportId { get; set; }
    public Sport Sport { get; set; } = null!;
    public int CoachId { get; set; }
    public User Coach { get; set; } = null!;
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public Level Level { get; set; }
    public int Capacity { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public ClassStatus Status { get; set; } = ClassStatus.Open;

    public List<ClassSession> Sessions { get; set; } = [];
    public List<ClassEnrollment> Enrollments { get; set; } = [];
}

// One concrete meeting of a class; the manager generates these when creating the class.
public class ClassSession
{
    public int Id { get; set; }
    public int ClassId { get; set; }
    public Class Class { get; set; } = null!;
    public int RoomId { get; set; }
    public Room Room { get; set; } = null!;
    public DateOnly Date { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Scheduled;
    public string? Note { get; set; }
}

public class ClassEnrollment // unique (ClassId, MemberId)
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

// Flow 4: attendance.
public class CheckIn // member arrives at the center (receptionist)
{
    public int Id { get; set; }
    public int MemberId { get; set; }
    public User Member { get; set; } = null!;
    public int CheckedInById { get; set; }
    public User CheckedInBy { get; set; } = null!;
    public DateTime CheckedInAt { get; set; } = DateTime.UtcNow;
}

public class Attendance // per class session (coach); unique (SessionId, MemberId)
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public ClassSession Session { get; set; } = null!;
    public int MemberId { get; set; }
    public User Member { get; set; } = null!;
    public AttendanceStatus Status { get; set; }
    public int MarkedById { get; set; }
    public User MarkedBy { get; set; } = null!;
    public DateTime MarkedAt { get; set; } = DateTime.UtcNow;
}
