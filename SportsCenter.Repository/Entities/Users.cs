namespace SportsCenter.Repository.Entities;

// Flow 1. Auth is handled by our own API (PasswordHash + JWT).
public class User
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string FullName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string? Phone { get; set; }
    public UserRole Role { get; set; }
    public bool IsActive { get; set; } = true; // soft delete
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public MemberProfile? MemberProfile { get; set; }
    public CoachProfile? CoachProfile { get; set; }
}

public class MemberProfile
{
    public int UserId { get; set; } // PK + FK
    public User User { get; set; } = null!;
    public DateOnly? DateOfBirth { get; set; }
    public string? Gender { get; set; }
    public string? Address { get; set; }
    public string? EmergencyContact { get; set; }
    public decimal? HeightCm { get; set; }
    public decimal? WeightKg { get; set; }
    public string? Goal { get; set; } // training goal, feeds AI recommendation
    public Level Level { get; set; }
}

public class CoachProfile
{
    public int UserId { get; set; } // PK + FK
    public User User { get; set; } = null!;
    public string? Bio { get; set; }
    public string? Certifications { get; set; }
}
