namespace SportsCenter.Repository.Entities;

// Flow 1 (plans/subscriptions) + Flow 3 (payments). Reports and invoices are derived from Payment, no own tables.
public class MembershipPlan
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int DurationDays { get; set; }
    public decimal Price { get; set; } // "học phí"
    public bool IsActive { get; set; } = true;
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
}

public class Payment
{
    public int Id { get; set; }
    public int MemberId { get; set; }
    public User Member { get; set; } = null!;
    public int? SubscriptionId { get; set; }
    public MemberSubscription? Subscription { get; set; }
    public int? ReceivedById { get; set; } // receptionist; null = paid online
    public User? ReceivedBy { get; set; }
    public decimal Amount { get; set; }
    public PaymentMethod Method { get; set; }
    public PaymentStatus Status { get; set; } = PaymentStatus.Paid;
    public string? InvoiceNumber { get; set; } // unique when set
    public string? Note { get; set; }
    public DateTime PaidAt { get; set; } = DateTime.UtcNow;
}
