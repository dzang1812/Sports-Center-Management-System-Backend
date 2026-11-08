using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

public interface IMemberTierRepository
{
    Task<decimal> GetLifetimeSpendAsync(int memberId, CancellationToken ct);
    Task<bool> NotificationExistsAsync(string deduplicationKey, CancellationToken ct);
}

public sealed class MemberTierRepository(AppDbContext db) : IMemberTierRepository
{
    // Money actually spent on services: wallet purchases net of refunds + paid direct (PayOS/counter) subscription payments.
    // Wallet top-ups are deposits, not spending — they count only once used in a purchase.
    public async Task<decimal> GetLifetimeSpendAsync(int memberId, CancellationToken ct)
    {
        var orders = await db.CommerceOrders.Where(o => o.MemberId == memberId)
            .SumAsync(o => (decimal?)(o.PaidAmount - o.RefundedAmount), ct) ?? 0m;
        var direct = await db.Payments.Where(p => p.MemberId == memberId && p.Status == PaymentStatus.Paid
                && p.Purpose == PaymentPurpose.LegacySubscription)
            .SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;
        return orders + direct;
    }

    // Also sees notifications added to this context but not yet saved (two purchases in one transaction).
    public async Task<bool> NotificationExistsAsync(string deduplicationKey, CancellationToken ct) =>
        db.Notifications.Local.Any(n => n.DeduplicationKey == deduplicationKey)
        || await db.Notifications.AnyAsync(n => n.DeduplicationKey == deduplicationKey, ct);
}
