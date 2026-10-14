using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<MemberProfile> MemberProfiles => Set<MemberProfile>();
    public DbSet<EmailToken> EmailTokens => Set<EmailToken>();
    public DbSet<CoachProfile> CoachProfiles => Set<CoachProfile>();
    public DbSet<Sport> Sports => Set<Sport>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<Class> Classes => Set<Class>();
    public DbSet<ClassSession> ClassSessions => Set<ClassSession>();
    public DbSet<ClassEnrollment> ClassEnrollments => Set<ClassEnrollment>();
    public DbSet<ClassBooking> ClassBookings => Set<ClassBooking>();
    public DbSet<CoachAvailability> CoachAvailabilities => Set<CoachAvailability>();
    public DbSet<PtBooking> PtBookings => Set<PtBooking>();
    public DbSet<CheckIn> CheckIns => Set<CheckIn>();
    public DbSet<Attendance> Attendances => Set<Attendance>();
    public DbSet<CoachSessionAttendance> CoachSessionAttendances => Set<CoachSessionAttendance>();
    public DbSet<MembershipPlan> MembershipPlans => Set<MembershipPlan>();
    public DbSet<MemberSubscription> MemberSubscriptions => Set<MemberSubscription>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Exercise> Exercises => Set<Exercise>();
    public DbSet<TrainingPlan> TrainingPlans => Set<TrainingPlan>();
    public DbSet<TrainingPlanItem> TrainingPlanItems => Set<TrainingPlanItem>();
    public DbSet<TrainingResult> TrainingResults => Set<TrainingResult>();
    public DbSet<ProgressEvaluation> ProgressEvaluations => Set<ProgressEvaluation>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<SupportRequest> SupportRequests => Set<SupportRequest>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<AiSuggestion> AiSuggestions => Set<AiSuggestion>();
    public DbSet<AiChatMessage> AiChatMessages => Set<AiChatMessage>();

    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        c.Properties<Enum>().HaveConversion<string>();
        c.Properties<decimal>().HavePrecision(12, 2);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Rows are soft-deleted / cancelled, never cascaded away by accident.
        foreach (var fk in b.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
            fk.DeleteBehavior = DeleteBehavior.Restrict;

        b.Entity<User>().HasIndex(u => u.Email).IsUnique();
        b.Entity<EmailToken>().HasIndex(t => t.TokenHash).IsUnique();
        b.Entity<MemberProfile>().HasKey(p => p.UserId);
        b.Entity<MemberProfile>().Property(p => p.GymCardCode).HasMaxLength(64);
        b.Entity<MemberProfile>().HasIndex(p => p.GymCardCode).IsUnique()
            .HasFilter("\"GymCardCode\" IS NOT NULL");
        b.Entity<CoachProfile>().HasKey(p => p.UserId);
        b.Entity<CoachProfile>().HasMany(p => p.Sports).WithMany();

        b.Entity<ClassEnrollment>().HasIndex(e => new { e.ClassId, e.MemberId }).IsUnique();
        b.Entity<Sport>().HasIndex(s => s.Name).IsUnique();
        b.Entity<Room>().HasIndex(r => r.Name).IsUnique();
        b.Entity<ClassSession>().HasIndex(s => new { s.ClassId, s.Date, s.StartTime }).IsUnique();
        b.Entity<ClassBooking>().HasIndex(x => new { x.SessionId, x.MemberId })
            .IsUnique().HasFilter("\"Status\" = 'Confirmed'");
        b.Entity<PtBooking>().HasIndex(x => new { x.CoachId, x.Date, x.StartTime })
            .IsUnique().HasFilter("\"Status\" = 'Confirmed'");
        b.Entity<CoachAvailability>().HasIndex(x => new { x.CoachId, x.DayOfWeek, x.StartTime })
            .IsUnique().HasFilter("\"IsActive\"");
        b.Entity<Attendance>().HasIndex(a => new { a.SessionId, a.MemberId }).IsUnique()
            .HasFilter("\"SessionId\" IS NOT NULL");
        b.Entity<Attendance>().HasIndex(a => new { a.PtBookingId, a.MemberId }).IsUnique()
            .HasFilter("\"PtBookingId\" IS NOT NULL");
        b.Entity<CoachSessionAttendance>().HasIndex(a => new { a.SessionId, a.CoachId }).IsUnique();
        b.Entity<ClassSession>().Property(x => x.CheckInCodeHash).HasMaxLength(64);
        b.Entity<Notification>().HasIndex(n => n.DeduplicationKey).IsUnique();
        b.Entity<Payment>().HasIndex(p => p.InvoiceNumber).IsUnique();
        b.Entity<Payment>().HasIndex(p => p.PayOsOrderCode).IsUnique();
        b.Entity<CheckIn>().HasIndex(x => x.MemberId).IsUnique()
            .HasFilter("\"CheckedOutAt\" IS NULL");
        b.Entity<CheckIn>().Property(x => x.CheckInSource).HasMaxLength(100);
        b.Entity<CheckIn>().Property(x => x.CheckOutSource).HasMaxLength(100);

        b.Entity<Sport>().ToTable(t => t.HasCheckConstraint(
            "CK_Sport_Capability", "\"HasClasses\" OR \"HasPt\""));
        b.Entity<Room>().ToTable(t => t.HasCheckConstraint("CK_Room_Capacity", "\"Capacity\" > 0"));
        b.Entity<Class>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_Class_Capacity", "\"Capacity\" > 0");
            t.HasCheckConstraint("CK_Class_Dates", "\"EndDate\" > \"StartDate\"");
        });
        b.Entity<ClassSession>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_ClassSession_Times", "\"EndTime\" > \"StartTime\"");
            t.HasCheckConstraint("CK_ClassSession_Capacity", "\"Capacity\" > 0");
        });
        b.Entity<CoachAvailability>().ToTable(t => t.HasCheckConstraint(
            "CK_CoachAvailability_Times", "\"EndTime\" > \"StartTime\""));
        b.Entity<PtBooking>().ToTable(t => t.HasCheckConstraint(
            "CK_PtBooking_OneHour", "\"EndTime\" = \"StartTime\" + interval '1 hour'"));
        b.Entity<MembershipPlan>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_MembershipPlan_PtCredits", "\"PtSessionCredits\" >= 0");
            t.HasCheckConstraint("CK_MembershipPlan_Duration",
                "(\"DurationDays\" > 0 AND \"DurationMonths\" = 0) OR (\"DurationDays\" = 0 AND \"DurationMonths\" > 0)");
        });
        b.Entity<MemberSubscription>().ToTable(t => t.HasCheckConstraint(
            "CK_MemberSubscription_RemainingPt", "\"RemainingPtSessions\" >= 0"));
        b.Entity<Attendance>().ToTable(t => t.HasCheckConstraint(
            "CK_Attendance_Target", "(\"SessionId\" IS NULL) <> (\"PtBookingId\" IS NULL)"));
        b.Entity<CoachSessionAttendance>().ToTable(t => t.HasCheckConstraint(
            "CK_CoachSessionAttendance_Status", "\"Status\" IN ('Present', 'Late')"));
        b.Entity<CheckIn>().ToTable(t => t.HasCheckConstraint(
            "CK_CheckIn_CheckOutTime", "\"CheckedOutAt\" IS NULL OR \"CheckedOutAt\" >= \"CheckedInAt\""));

        b.Entity<TrainingPlan>().ToTable(t => t.HasCheckConstraint(
            "CK_TrainingPlan_Target", "(\"MemberId\" IS NULL) <> (\"ClassId\" IS NULL)"));
        b.Entity<TrainingPlanItem>().HasOne(i => i.Plan).WithMany(p => p.Items)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<AuditLog>().Property(a => a.Details).HasColumnType("jsonb");
        b.Entity<AiSuggestion>().Property(a => a.Input).HasColumnType("jsonb");
        b.Entity<AiSuggestion>().Property(a => a.Output).HasColumnType("jsonb");
    }
}
