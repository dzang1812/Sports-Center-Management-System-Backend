namespace SportsCenter.Repository.Entities;

// Stored as strings in the DB (see AppDbContext.ConfigureConventions).
public enum UserRole { Manager, Coach, Member, Receptionist }
public enum Level { Beginner, Intermediate, Advanced }
public enum ClassStatus { Open, Closed, Cancelled }
public enum SessionStatus { Scheduled, Completed, Cancelled }
public enum EnrollmentStatus { Enrolled, Cancelled }
<<<<<<< Updated upstream
public enum SubscriptionStatus { Active, Expired, Cancelled }
=======
public enum BookingStatus { Confirmed, Cancelled, Completed }
public enum SportStatus { Active, Inactive }
public enum RoomStatus { Active, Maintenance, Inactive }
public enum SubscriptionStatus { Pending, Active, Expired, Cancelled }
>>>>>>> Stashed changes
public enum PaymentMethod { Cash, BankTransfer, Card }
public enum PaymentStatus { Pending, Paid, Failed, Refunded }
public enum AttendanceStatus { Present, Absent, Late, Excused }
public enum NotificationType { General, ScheduleChange, Homework, SubscriptionExpiry }
public enum SupportStatus { Open, InProgress, Resolved }
public enum AiSuggestionStatus { Suggested, Accepted, Rejected }
public enum ChatRole { User, Assistant }
