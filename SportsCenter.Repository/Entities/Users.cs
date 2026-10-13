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
    public DateOnly? DateOfBirth { get; set; }
    public Gender? Gender { get; set; }
    public string? AvatarUrl { get; set; }
    public bool MustChangePassword { get; set; } // temp password set by Manager/receptionist
    public bool EmailVerified { get; set; }
    public bool IsActive { get; set; } = true; // soft delete
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public MemberProfile? MemberProfile { get; set; }
    public CoachProfile? CoachProfile { get; set; }
}

public class MemberProfile
{
    public int UserId { get; set; } // PK + FK
    public User User { get; set; } = null!;
    public string? GymCardCode { get; set; }
    public string? Address { get; set; }
    public string? EmergencyContact { get; set; }
    public decimal? HeightCm { get; set; }
    public decimal? WeightKg { get; set; }
    public TrainingGoal? Goal { get; set; } // feeds AI recommendation
    public Level Level { get; set; }
}

public class CoachProfile
{
    public int UserId { get; set; } // PK + FK
    public User User { get; set; } = null!;
    public string? Bio { get; set; }
    public string? Certifications { get; set; }
    public List<Sport> Sports { get; set; } = []; // specialties; Gym/Boxing coaches can take PT
}

// One-time link token sent by email (verify address / reset password). Only the SHA-256 hash is stored.
public class EmailToken
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public TokenPurpose Purpose { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
}
