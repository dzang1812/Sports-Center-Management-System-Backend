using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

public class MembershipRepository(AppDbContext db) : IMembershipRepository
{
    public Task<List<MembershipPlan>> ListPlansAsync(bool activeOnly, CancellationToken ct) =>
        (activeOnly ? db.MembershipPlans.Where(x => x.IsActive) : db.MembershipPlans)
            .Include(x => x.Sport).AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);

    public Task<MembershipPlan?> GetPlanAsync(int id, CancellationToken ct) =>
        db.MembershipPlans.Include(x => x.Sport).FirstOrDefaultAsync(x => x.Id == id, ct);

    public Task<List<Sport>> ListSportsAsync(CancellationToken ct) =>
        db.Sports.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);

    public Task<bool> PlanNameExistsAsync(string name, CancellationToken ct) =>
        db.MembershipPlans.AnyAsync(x => x.Name.ToLower() == name.ToLower(), ct);

    public Task<bool> PlanNameExistsForOtherAsync(string name, int excludeId, CancellationToken ct) =>
        db.MembershipPlans.AnyAsync(x => x.Id != excludeId && x.Name.ToLower() == name.ToLower(), ct);

    public Task<bool> PlanHasSubscriptionsAsync(int planId, CancellationToken ct) =>
        db.MemberSubscriptions.AnyAsync(x => x.PlanId == planId, ct);

    public async Task AddPlanAsync(MembershipPlan plan, CancellationToken ct) => await db.MembershipPlans.AddAsync(plan, ct);

    public Task<MemberSubscription?> GetLatestSubscriptionAsync(int memberId, int planId, CancellationToken ct) =>
        db.MemberSubscriptions.Where(x => x.MemberId == memberId && x.PlanId == planId &&
                                          x.Status == SubscriptionStatus.Active)
            .OrderByDescending(x => x.EndDate).FirstOrDefaultAsync(ct);

    public Task<List<MemberSubscription>> ListSubscriptionsAsync(int memberId, CancellationToken ct) =>
        db.MemberSubscriptions.Include(x => x.Plan).ThenInclude(x => x.Sport).AsNoTracking()
            .Where(x => x.MemberId == memberId).OrderByDescending(x => x.EndDate).ToListAsync(ct);

    public async Task<Dictionary<int, PaymentStatus>> GetLatestPaymentStatusesAsync(
        IReadOnlyCollection<int> subscriptionIds, CancellationToken ct)
    {
        if (subscriptionIds.Count == 0) return [];
        return await db.Payments.AsNoTracking()
            .Where(x => x.SubscriptionId.HasValue && subscriptionIds.Contains(x.SubscriptionId.Value))
            .GroupBy(x => x.SubscriptionId!.Value)
            .Select(g => g.OrderByDescending(x => x.Id)
                .Select(x => new { SubscriptionId = g.Key, x.Status }).First())
            .ToDictionaryAsync(x => x.SubscriptionId, x => x.Status, ct);
    }

    public Task<MemberSubscription?> FindOwnedSubscriptionAsync(int memberId, int subscriptionId, CancellationToken ct) =>
        db.MemberSubscriptions.Include(x => x.Plan)
            .SingleOrDefaultAsync(x => x.Id == subscriptionId && x.MemberId == memberId, ct);

    public Task<SubscriptionCancellationBlockers> GetCancellationBlockersAsync(int subscriptionId, DateOnly today,
        TimeOnly localTime, CancellationToken ct) =>
        db.MemberSubscriptions.Where(x => x.Id == subscriptionId).Select(x => new SubscriptionCancellationBlockers(
            db.CheckIns.Any(v => v.SubscriptionId == x.Id && v.CheckedOutAt == null),
            db.ClassBookings.Count(b => b.SubscriptionId == x.Id && b.Status == BookingStatus.Confirmed &&
                b.Session.Status == SessionStatus.Scheduled &&
                (b.Session.Date > today || b.Session.Date == today && b.Session.EndTime > localTime)),
            db.PtBookings.Count(b => b.SubscriptionId == x.Id && b.Status == BookingStatus.Confirmed &&
                (b.Date > today || b.Date == today && b.EndTime > localTime)),
            db.Payments.Any(p => p.SubscriptionId == x.Id && p.Status == PaymentStatus.Pending)))
            .SingleAsync(ct);

    public Task<Payment?> GetPendingSubscriptionPaymentAsync(int memberId, int planId, CancellationToken ct) =>
        db.Payments.Include(x => x.Subscription!).ThenInclude(x => x.Plan).FirstOrDefaultAsync(x => x.MemberId == memberId && x.Status == PaymentStatus.Pending && x.Subscription != null && x.Subscription.PlanId == planId, ct);

    public Task<bool> HasPendingSubscriptionPaymentAsync(int memberId, int planId, CancellationToken ct) =>
        db.Payments.AnyAsync(x => x.MemberId == memberId && x.Status == PaymentStatus.Pending &&
                                  x.Subscription != null && x.Subscription.PlanId == planId, ct);

    public async Task AddSubscriptionAsync(MemberSubscription subscription, CancellationToken ct) =>
        await db.MemberSubscriptions.AddAsync(subscription, ct);

    public async Task AddPaymentAsync(Payment payment, CancellationToken ct) => await db.Payments.AddAsync(payment, ct);

    public Task<Payment?> GetPaymentWithDetailsAsync(int id, CancellationToken ct) =>
        db.Payments.Include(x => x.Member).Include(x => x.ReceivedBy)
            .Include(x => x.Subscription!).ThenInclude(x => x.Plan)
            .FirstOrDefaultAsync(x => x.Id == id, ct);

    // Cash/Card payments a Receptionist started at the desk but hasn't confirmed receiving money for yet.
    public Task<List<Payment>> ListPendingCounterPaymentsAsync(CancellationToken ct) =>
        db.Payments.AsNoTracking().Where(x => x.Status == PaymentStatus.Pending && x.Method != PaymentMethod.BankTransfer)
            .Include(x => x.Member).Include(x => x.ReceivedBy)
            .Include(x => x.Subscription!).ThenInclude(x => x.Plan)
            .OrderByDescending(x => x.Id).ToListAsync(ct);

    // Bank-transfer payments that got a PayOS link and are still waiting on it.
    public Task<List<int>> ListPendingPayOsPaymentIdsAsync(CancellationToken ct) =>
        db.Payments.AsNoTracking().Where(x => x.Status == PaymentStatus.Pending && x.Method == PaymentMethod.BankTransfer && x.CheckoutUrl != null)
            .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);

    // Read straight from the DB: the tracked entity may predate a webhook committed by another request.
    public Task<PaymentStatus?> GetPaymentStatusAsync(long orderCode, CancellationToken ct) =>
        db.Payments.AsNoTracking().Where(x => x.PayOsOrderCode == orderCode).Select(x => (PaymentStatus?)x.Status).FirstOrDefaultAsync(ct);

    public Task<PayOsPaymentState?> GetPayOsStateAsync(int paymentId, CancellationToken ct) =>
        db.Payments.AsNoTracking().Where(x => x.Id == paymentId)
            .Select(x => new PayOsPaymentState(x.MemberId, x.PayOsOrderCode, x.Status, x.Method, x.CheckoutUrl != null)).FirstOrDefaultAsync(ct);

    public Task<Payment?> GetPaymentByOrderCodeAsync(long orderCode, CancellationToken ct) =>
        db.Payments.Include(x => x.Subscription).FirstOrDefaultAsync(x => x.PayOsOrderCode == orderCode, ct);

    public Task<PaymentKey?> GetPaymentKeyAsync(int id, CancellationToken ct) =>
        db.Payments.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new PaymentKey(x.MemberId)).FirstOrDefaultAsync(ct);

    public Task<PaymentKey?> GetPaymentKeyByOrderCodeAsync(long orderCode, CancellationToken ct) =>
        db.Payments.AsNoTracking().Where(x => x.PayOsOrderCode == orderCode)
            .Select(x => new PaymentKey(x.MemberId)).FirstOrDefaultAsync(ct);

    public Task AddAuditAsync(int actorId, string action, string entityName, int entityId, object details, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog
        {
            UserId = actorId,
            Action = action,
            EntityName = entityName,
            EntityId = entityId.ToString(),
            Details = JsonSerializer.Serialize(details),
            CreatedAt = DateTime.UtcNow,
        });
        return Task.CompletedTask;
    }

    public async Task<RevenueReportData> GetRevenueReportAsync(DateOnly from, DateOnly to, DateTime fromUtc, DateTime toUtcExclusive,
        DateOnly today, CancellationToken ct)
    {
        var revenue = await db.Payments
            .Where(p => p.Purpose != PaymentPurpose.WalletTopUp && p.Status == PaymentStatus.Paid && p.PaidAt >= fromUtc && p.PaidAt < toUtcExclusive)
            .GroupBy(_ => 1).Select(g => new { Total = g.Sum(x => x.Amount), Count = g.Count() })
            .FirstOrDefaultAsync(ct);
        var newMembers = await db.Users.CountAsync(u => u.Role == UserRole.Member && u.CreatedAt >= fromUtc && u.CreatedAt < toUtcExclusive, ct);
        var newSubscriptions = await db.MemberSubscriptions.CountAsync(s => s.StartDate >= from && s.StartDate <= to, ct);
        var activeSubscriptions = await db.MemberSubscriptions.CountAsync(s => s.Status == SubscriptionStatus.Active && s.EndDate >= today, ct);
        var expiredSubscriptions = await db.MemberSubscriptions.CountAsync(s =>
            s.Status == SubscriptionStatus.Expired || (s.Status == SubscriptionStatus.Active && s.EndDate < today), ct);
        var newClassBookings = await db.ClassBookings.CountAsync(b =>
            b.Status != BookingStatus.Cancelled && b.CreatedAt >= fromUtc && b.CreatedAt < toUtcExclusive, ct);

        var orders = await db.CommerceOrders.Where(x => x.CreatedAt >= fromUtc && x.CreatedAt < toUtcExclusive)
            .GroupBy(_ => 1).Select(g => new { Total = g.Sum(x => x.PaidAmount - x.RefundedAmount), Count = g.Count() }).FirstOrDefaultAsync(ct);
        return new RevenueReportData((revenue?.Total ?? 0) + (orders?.Total ?? 0), (revenue?.Count ?? 0) + (orders?.Count ?? 0), newMembers, newSubscriptions,
            activeSubscriptions, expiredSubscriptions, newClassBookings);
    }

    private static readonly string[] MoneyLogKinds = ["CashTopUp", "OnlineTopUp", "Purchase", "Refund"];

    // Wallet top-ups, purchases and refunds (the ledger), plus top-ups still Pending/Failed so money that never arrived shows too.
    public async Task<List<MoneyLogRow>> ListMoneyLogAsync(DateTime fromUtc, DateTime toUtcExclusive, string search, CancellationToken ct)
    {
        var term = search.Trim().ToLower();
        var memberId = int.TryParse(term, out var id) ? id : 0;

        var entries = db.WalletEntries.AsNoTracking()
            .Where(x => MoneyLogKinds.Contains(x.Kind) && x.CreatedAt >= fromUtc && x.CreatedAt < toUtcExclusive);
        if (term.Length > 0)
            entries = entries.Where(x => x.MemberId == memberId || x.Member.FullName.ToLower().Contains(term)
                || x.Member.Email.ToLower().Contains(term) || x.Actor.FullName.ToLower().Contains(term));
        var ledger = await entries.Select(x => new MoneyLogRow("wallet", x.Id, x.CreatedAt, x.Kind, PaymentStatus.Paid,
                x.MemberId, x.Member.FullName, x.ActorId, x.Actor.FullName, x.Actor.Role, x.Amount, x.BalanceAfter,
                x.Payment != null ? x.Payment.Method : null, x.Reference, x.OrderId,
                x.Order != null ? x.Order.Kind : null, x.Order != null ? x.Order.Snapshot : null,
                x.PaymentId, x.Payment != null ? x.Payment.InvoiceNumber : null,
                x.Kind == "Purchase" ? db.VoucherRedemptions.Where(v => v.OrderId == x.OrderId).Select(v => v.Voucher.Code).FirstOrDefault() : null,
                x.Kind == "Purchase" ? db.VoucherRedemptions.Where(v => v.OrderId == x.OrderId).Select(v => (decimal?)v.ListPrice).FirstOrDefault() : null,
                x.Kind == "Purchase" ? db.VoucherRedemptions.Where(v => v.OrderId == x.OrderId).Select(v => (decimal?)v.DiscountAmount).FirstOrDefault() : null))
            .ToListAsync(ct);

        // Payment has no CreatedAt: PaidAt defaults to creation time and stays so until the payment is paid.
        var payments = db.Payments.AsNoTracking().Where(p => p.Purpose == PaymentPurpose.WalletTopUp
            && (p.Status == PaymentStatus.Pending || p.Status == PaymentStatus.Failed) && p.PaidAt >= fromUtc && p.PaidAt < toUtcExclusive);
        if (term.Length > 0)
            payments = payments.Where(p => p.MemberId == memberId || p.Member.FullName.ToLower().Contains(term)
                || p.Member.Email.ToLower().Contains(term) || (p.ReceivedBy != null && p.ReceivedBy.FullName.ToLower().Contains(term)));
        var unpaid = await payments.Select(p => new MoneyLogRow("payment", p.Id, p.PaidAt,
                p.Method == PaymentMethod.Cash ? "CashTopUp" : "OnlineTopUp", p.Status, p.MemberId, p.Member.FullName,
                p.ReceivedById ?? p.MemberId, p.ReceivedBy != null ? p.ReceivedBy.FullName : p.Member.FullName,
                p.ReceivedBy != null ? p.ReceivedBy.Role : p.Member.Role, p.Amount, null, p.Method, $"payment:{p.Id}",
                null, null, null, p.Id, p.InvoiceNumber, null, null, null))
            .ToListAsync(ct);
        return [.. ledger, .. unpaid];
    }

    // Same revenue definition as GetRevenueReportAsync, one row per payment/order so it can be split by item.
    public async Task<List<RevenueSourceRow>> ListRevenueSourcesAsync(DateTime fromUtc, DateTime toUtcExclusive, CancellationToken ct)
    {
        var payments = await db.Payments.AsNoTracking()
            .Where(p => p.Purpose != PaymentPurpose.WalletTopUp && p.Status == PaymentStatus.Paid && p.PaidAt >= fromUtc && p.PaidAt < toUtcExclusive)
            .Select(p => new RevenueSourceRow(CommerceOrderKind.Membership, null, p.SubscriptionId != null ? p.Subscription!.PlanId : null,
                p.SubscriptionId != null ? p.Subscription!.Plan.Name : null, p.Amount))
            .ToListAsync(ct);
        var orders = await db.CommerceOrders.AsNoTracking().Where(x => x.CreatedAt >= fromUtc && x.CreatedAt < toUtcExclusive)
            .Select(x => new RevenueSourceRow(x.Kind, x.Snapshot, null, null, x.PaidAmount - x.RefundedAmount))
            .ToListAsync(ct);
        return [.. payments, .. orders];
    }

    public async Task<WalletReceiptReportData> GetWalletReceiptsAsync(DateTime fromUtc, DateTime toUtcExclusive,
        string search, int page, int pageSize, CancellationToken ct)
    {
        // Wallet funding is cash-in, not service revenue. Never include pending/failed payments or refunds.
        var receipts = db.Payments.AsNoTracking().Where(p => p.Purpose == PaymentPurpose.WalletTopUp
            && p.Status == PaymentStatus.Paid && p.PaidAt >= fromUtc && p.PaidAt < toUtcExclusive);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            var memberId = int.TryParse(term, out var id) ? id : 0;
            receipts = receipts.Where(p => p.MemberId == memberId || (p.InvoiceNumber != null && p.InvoiceNumber.ToLower().Contains(term))
                || p.Member.FullName.ToLower().Contains(term) || p.Member.Email.ToLower().Contains(term)
                || (p.ReceivedBy != null && p.ReceivedBy.FullName.ToLower().Contains(term)));
        }
        var summary = await receipts.GroupBy(_ => 1).Select(g => new
        {
            Total = g.Count(), Amount = g.Sum(p => p.Amount),
            Cash = g.Sum(p => p.Method == PaymentMethod.Cash ? p.Amount : 0),
            Bank = g.Sum(p => p.Method == PaymentMethod.BankTransfer ? p.Amount : 0),
        }).FirstOrDefaultAsync(ct);
        var items = await receipts.OrderByDescending(p => p.PaidAt).ThenByDescending(p => p.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(p => new WalletReceiptRow(p.Id, p.InvoiceNumber, p.MemberId, p.Member.FullName,
                p.ReceivedById, p.ReceivedBy != null ? p.ReceivedBy.FullName : null, p.Amount, p.Method, p.PaidAt)).ToListAsync(ct);
        return new(items, summary?.Total ?? 0, summary?.Amount ?? 0, summary?.Cash ?? 0, summary?.Bank ?? 0);
    }

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
