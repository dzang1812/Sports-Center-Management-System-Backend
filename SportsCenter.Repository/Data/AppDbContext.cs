using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<MemberProfile> MemberProfiles => Set<MemberProfile>();
    public DbSet<CoachProfile> CoachProfiles => Set<CoachProfile>();
    public DbSet<Sport> Sports => Set<Sport>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<Class> Classes => Set<Class>();
    public DbSet<ClassSession> ClassSessions => Set<ClassSession>();
    public DbSet<ClassEnrollment> ClassEnrollments => Set<ClassEnrollment>();
    public DbSet<CheckIn> CheckIns => Set<CheckIn>();
    public DbSet<Attendance> Attendances => Set<Attendance>();
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
        b.Entity<MemberProfile>().HasKey(p => p.UserId);
        b.Entity<CoachProfile>().HasKey(p => p.UserId);

        b.Entity<ClassEnrollment>().HasIndex(e => new { e.ClassId, e.MemberId }).IsUnique();
        b.Entity<Attendance>().HasIndex(a => new { a.SessionId, a.MemberId }).IsUnique();
        b.Entity<Payment>().HasIndex(p => p.InvoiceNumber).IsUnique();
        b.Entity<Payment>().HasIndex(p => p.PayOsOrderCode).IsUnique();

        b.Entity<TrainingPlan>().ToTable(t => t.HasCheckConstraint(
            "CK_TrainingPlan_Target", "(\"MemberId\" IS NULL) <> (\"ClassId\" IS NULL)"));
        b.Entity<TrainingPlanItem>().HasOne(i => i.Plan).WithMany(p => p.Items)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<AuditLog>().Property(a => a.Details).HasColumnType("jsonb");
        b.Entity<AiSuggestion>().Property(a => a.Input).HasColumnType("jsonb");
        b.Entity<AiSuggestion>().Property(a => a.Output).HasColumnType("jsonb");
    }
}
