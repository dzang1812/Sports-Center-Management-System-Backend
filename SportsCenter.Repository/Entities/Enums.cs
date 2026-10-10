namespace SportsCenter.Repository.Entities;

// Stored as strings in the DB (see AppDbContext.ConfigureConventions).
public enum UserRole { Manager, Coach, Member, Receptionist }
public enum TokenPurpose { VerifyEmail, ResetPassword }
public enum Level { Beginner, Intermediate, Advanced }
public enum TrainingGoal { WeightLoss, MuscleGain, Endurance, GeneralHealth }
public enum Gender { Male, Female, Other }
public enum ClassStatus { Draft, Open, Closed, Cancelled }
public enum SessionStatus { Scheduled, Completed, Cancelled }
public enum EnrollmentStatus { Enrolled, Cancelled }
public enum BookingStatus { Confirmed, Cancelled, Completed }
public enum SportStatus { Active, Inactive }
public enum RoomStatus { Active, Maintenance, Inactive }
public enum SubscriptionStatus { Pending, Active, Expired, Cancelled }
public enum PaymentMethod { Cash, BankTransfer, Card }
public enum PaymentStatus { Pending, Paid, Failed, Refunded }
public enum AttendanceStatus { Present, Absent, Late, Excused }
public enum NotificationType { General, ScheduleChange, Booking, Homework, SubscriptionExpiry }
public enum SupportStatus { Open, InProgress, Resolved }
public enum AiSuggestionStatus { Suggested, Accepted, Rejected }
public enum ChatRole { User, Assistant }
