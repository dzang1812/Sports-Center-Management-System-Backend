namespace SportsCenter.Repository.Entities;

// Flow 1 (plans/subscriptions) + Flow 3 (payments). Reports and invoices are derived from Payment, no own tables.
public class MembershipPlan
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int DurationDays { get; set; }
    public int DurationMonths { get; set; }
    public decimal Price { get; set; } // "học phí"
    public bool IsActive { get; set; } = true;
    public int? SportId { get; set; }
    public Sport? Sport { get; set; }
    public bool IncludesClasses { get; set; }
    public bool IncludesGymAccess { get; set; }
    public int PtSessionCredits { get; set; }
}

public class MemberSubscription
{
    public int Id { get; set; }
    public int MemberId { get; set; }
    public User Member { get; set; } = null!;
    public int PlanId { get; set; }
    public MembershipPlan Plan { get; set; } = null!;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; } // renewal = new row starting after the previous EndDate
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Active;
    public bool IncludesGymAccess { get; set; }
    public int RemainingPtSessions { get; set; }
}

// All payments go through PayOS (bank transfer / VietQR), Member self-service or Receptionist-assisted.
// Status starts Pending at link creation; the PayOS webhook flips it to Paid/Failed (see SubscriptionService).
public class Payment
{
    public int Id { get; set; }
    public int MemberId { get; set; }
    public User Member { get; set; } = null!;
    public int? SubscriptionId { get; set; }
    public MemberSubscription? Subscription { get; set; }
    public int? ReceivedById { get; set; } // Receptionist who set up the link for the member at the desk, if any
    public User? ReceivedBy { get; set; }
    public decimal Amount { get; set; }
    public PaymentMethod Method { get; set; } = PaymentMethod.BankTransfer;
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public string? InvoiceNumber { get; set; } // set once Paid; unique when set
    public string? Note { get; set; }
    public DateTime PaidAt { get; set; } = DateTime.UtcNow; // meaningful only once Status = Paid

    public long PayOsOrderCode { get; set; } // = Id, sent to PayOS as orderCode (already globally unique)
    public string? PayOsPaymentLinkId { get; set; }
    public string? CheckoutUrl { get; set; }
    public string? QrCode { get; set; }
}
