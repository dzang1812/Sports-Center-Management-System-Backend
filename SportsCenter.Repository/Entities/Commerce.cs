namespace SportsCenter.Repository.Entities;

public enum ClassBookingMode { LegacySession, CourseEnrollment }
public enum PtFundingSource { LegacyCredit, PackageCredit, Wallet }
public enum PaymentPurpose { LegacySubscription, WalletTopUp }
public enum CommerceOrderKind { Course, PtSingle, PtPackage }

public class MemberWallet
{
    public int MemberId { get; set; }
    public User Member { get; set; } = null!;
    public decimal Balance { get; set; }
}

// Immutable entries: corrections/refunds are additional entries, never edits to history.
public class WalletEntry
{
    public int Id { get; set; }
    public int MemberId { get; set; }
    public User Member { get; set; } = null!;
    public int ActorId { get; set; }
    public User Actor { get; set; } = null!;
    public decimal Amount { get; set; } // signed VND
    public decimal BalanceAfter { get; set; }
    public string Kind { get; set; } = "";
    public string Reference { get; set; } = "";
    public int? OrderId { get; set; }
    public CommerceOrder? Order { get; set; }
    public int? PaymentId { get; set; }
    public Payment? Payment { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CommerceOrder
{
    public int Id { get; set; }
    public int MemberId { get; set; }
    public User Member { get; set; } = null!;
    public int ActorId { get; set; }
    public User Actor { get; set; } = null!;
    public CommerceOrderKind Kind { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal RefundedAmount { get; set; }
    public string Snapshot { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
}

// Server-side replay record, scoped by actor + operation + key; saved in the purchase transaction.
public class CommerceRequest
{
    public int Id { get; set; }
    public int ActorId { get; set; }
    public string Operation { get; set; } = "";
    public string Key { get; set; } = "";
    public string PayloadHash { get; set; } = "";
    public string ResultJson { get; set; } = "";
}

// One purchased unit, including any replacement session. Value is fixed at purchase.
public class CourseEntitlement
{
    public int Id { get; set; }
    public int EnrollmentId { get; set; }
    public ClassEnrollment Enrollment { get; set; } = null!;
    public int OrderId { get; set; }
    public CommerceOrder Order { get; set; } = null!;
    public int OriginalSessionId { get; set; }
    public ClassSession OriginalSession { get; set; } = null!;
    public int CurrentSessionId { get; set; }
    public ClassSession CurrentSession { get; set; } = null!;
    public decimal Value { get; set; }
    public bool Refunded { get; set; }
}
