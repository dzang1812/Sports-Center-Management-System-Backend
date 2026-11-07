using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.Services.Membership;

public record RevenueReport(DateOnly From, DateOnly To, decimal TotalRevenue, int PaymentCount, int NewMembers,
    int NewSubscriptions, int ActiveSubscriptions, int ExpiredSubscriptions, int NewClassBookings);
// ItemId is the plan / course / PT package id; null for single PT sessions (one line for all of them).
public record RevenueItem(CommerceOrderKind Kind, int? ItemId, string Name, int Orders, decimal Revenue);
public record RevenueByItemReport(DateOnly From, DateOnly To, decimal TotalRevenue, List<RevenueItem> Items);
public enum MoneyLogType { CashTopUp, OnlineTopUp, Purchase, Refund }
// Amount is signed (+ into the wallet, − out of it). Pending/Failed top-ups never reached the wallet: BalanceAfter is null.
public record MoneyLogItem(string Source, int SourceId, DateTime At, MoneyLogType Type, PaymentStatus Status,
    int MemberId, string MemberName, int? ActorId, string? ActorName, UserRole? ActorRole, decimal Amount, decimal? BalanceAfter,
    string Method, string Description, CommerceOrderKind? ItemKind, string? ItemName, string Reference, int? OrderId, int? PaymentId,
    string? InvoiceNumber, string? VoucherCode, decimal? ListPrice, decimal? DiscountAmount);
public record MoneyLogSummary(decimal TopUpAmount, int TopUpCount, decimal SpentAmount, int PurchaseCount, decimal DiscountAmount,
    decimal RefundAmount, int RefundCount, int PendingCount, int FailedCount);
public record MoneyLogReport(DateOnly From, DateOnly To, MoneyLogSummary Summary, List<MoneyLogItem> Items, int Total, int Page, int PageSize);
public record WalletReceiptReport(DateOnly From, DateOnly To, List<WalletReceiptRow> Items, int Total, int Page, int PageSize,
    decimal TotalAmount, decimal CashAmount, decimal BankTransferAmount);

public interface IMembershipReportService
{
    Task<RevenueReport> GetRevenueReportAsync(int actorId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<RevenueByItemReport> GetRevenueByItemAsync(int actorId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<MoneyLogReport> GetMoneyLogAsync(int actorId, DateOnly from, DateOnly to, MoneyLogType? type, PaymentStatus? status,
        string? search, int page, int pageSize, CancellationToken ct);
    Task<WalletReceiptReport> GetWalletReceiptsAsync(int actorId, DateOnly from, DateOnly to,
        string? search, int page, int pageSize, CancellationToken ct);
}

public sealed class MembershipReportService(IMembershipRepository repo, IUserRepository users, IAppClock clock) : IMembershipReportService
{
    public async Task<WalletReceiptReport> GetWalletReceiptsAsync(int actorId, DateOnly from, DateOnly to,
        string? search, int page, int pageSize, CancellationToken ct)
    {
        var actor = await MembershipRules.EnsureActiveRoleAsync(users, actorId, UserRole.Manager);
        if (!actor.IsActive) throw SchedulingException.Forbidden("ACTOR_INACTIVE", "Tài khoản không hoạt động.");
        if (from == DateOnly.MinValue || to < from || to == DateOnly.MaxValue) throw new SchedulingException("REPORT_RANGE_INVALID", "Khoảng thời gian không hợp lệ.");
        if (search?.Length > 100) throw new SchedulingException("REPORT_SEARCH_INVALID", "Nội dung tìm kiếm tối đa 100 ký tự.");
        page = Math.Clamp(page, 1, 1000000); pageSize = Math.Clamp(pageSize, 1, 100);
        var data = await repo.GetWalletReceiptsAsync(clock.ToUtc(from, TimeOnly.MinValue),
            clock.ToUtc(to.AddDays(1), TimeOnly.MinValue), search?.Trim() ?? "", page, pageSize, ct);
        return new(from, to, data.Items, data.Total, page, pageSize, data.TotalAmount, data.CashAmount, data.BankTransferAmount);
    }

    public async Task<RevenueReport> GetRevenueReportAsync(int actorId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await MembershipRules.EnsureActiveRoleAsync(users, actorId, UserRole.Manager);
        if (to < from) throw new SchedulingException("REPORT_RANGE_INVALID", "Ngày kết thúc phải sau ngày bắt đầu.");

        var fromUtc = clock.ToUtc(from, TimeOnly.MinValue);
        var toUtcExclusive = clock.ToUtc(to.AddDays(1), TimeOnly.MinValue);
        var today = DateOnly.FromDateTime(clock.LocalNow);

        var data = await repo.GetRevenueReportAsync(from, to, fromUtc, toUtcExclusive, today, ct);
        return new RevenueReport(from, to, data.TotalRevenue, data.PaymentCount, data.NewMembers,
            data.NewSubscriptions, data.ActiveSubscriptions, data.ExpiredSubscriptions, data.NewClassBookings);
    }

    public async Task<RevenueByItemReport> GetRevenueByItemAsync(int actorId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await MembershipRules.EnsureActiveRoleAsync(users, actorId, UserRole.Manager);
        if (to < from) throw new SchedulingException("REPORT_RANGE_INVALID", "Ngày kết thúc phải sau ngày bắt đầu.");

        var rows = await repo.ListRevenueSourcesAsync(clock.ToUtc(from, TimeOnly.MinValue), clock.ToUtc(to.AddDays(1), TimeOnly.MinValue), ct);
        var items = GroupByItem(rows);
        return new(from, to, items.Sum(x => x.Revenue), items);
    }

    public async Task<MoneyLogReport> GetMoneyLogAsync(int actorId, DateOnly from, DateOnly to, MoneyLogType? type, PaymentStatus? status,
        string? search, int page, int pageSize, CancellationToken ct)
    {
        await MembershipRules.EnsureActiveRoleAsync(users, actorId, UserRole.Manager);
        if (to < from || to.DayNumber - from.DayNumber > 366) throw new SchedulingException("REPORT_RANGE_INVALID", "Khoảng thời gian không hợp lệ (tối đa 1 năm).");
        if (search?.Length > 100) throw new SchedulingException("REPORT_SEARCH_INVALID", "Nội dung tìm kiếm tối đa 100 ký tự.");
        page = Math.Clamp(page, 1, 1000000); pageSize = Math.Clamp(pageSize, 1, 100);

        // shortcut: both sources are merged, filtered and paged in memory; fine for one centre's volume within a 1-year range,
        // move paging into SQL (UNION view) if the ledger grows to hundreds of thousands of rows a year.
        var rows = await repo.ListMoneyLogAsync(clock.ToUtc(from, TimeOnly.MinValue), clock.ToUtc(to.AddDays(1), TimeOnly.MinValue), search ?? "", ct);
        var items = rows.Select(ToMoneyLogItem)
            .Where(x => (type is null || x.Type == type) && (status is null || x.Status == status))
            .OrderByDescending(x => x.At).ThenByDescending(x => x.SourceId).ToList();

        var paid = items.Where(x => x.Status == PaymentStatus.Paid).ToList();
        var topUps = paid.Where(x => x.Type is MoneyLogType.CashTopUp or MoneyLogType.OnlineTopUp).ToList();
        var purchases = paid.Where(x => x.Type == MoneyLogType.Purchase).ToList();
        var refunds = paid.Where(x => x.Type == MoneyLogType.Refund).ToList();
        var summary = new MoneyLogSummary(topUps.Sum(x => x.Amount), topUps.Count, -purchases.Sum(x => x.Amount), purchases.Count,
            purchases.Sum(x => x.DiscountAmount ?? 0), refunds.Sum(x => x.Amount), refunds.Count,
            items.Count(x => x.Status == PaymentStatus.Pending), items.Count(x => x.Status == PaymentStatus.Failed));
        return new(from, to, summary, items.Skip((page - 1) * pageSize).Take(pageSize).ToList(), items.Count, page, pageSize);
    }

    internal static MoneyLogItem ToMoneyLogItem(MoneyLogRow r)
    {
        var type = Enum.Parse<MoneyLogType>(r.Kind);
        var (_, snapshotName) = r.OrderSnapshot is null ? (null, null) : ReadSnapshotItem(r.OrderSnapshot);
        var itemName = r.OrderKind is { } kind ? snapshotName ?? DefaultName(kind) : null;
        var method = type is MoneyLogType.Purchase or MoneyLogType.Refund ? "Wallet" : (r.Method ?? PaymentMethod.BankTransfer).ToString();
        return new MoneyLogItem(r.Source, r.SourceId, r.At, type, r.Status, r.MemberId, r.MemberName, r.ActorId, r.ActorName, r.ActorRole,
            r.Amount, r.BalanceAfter, method, Describe(type, r, itemName), r.OrderKind, itemName, r.Reference, r.OrderId, r.PaymentId,
            r.InvoiceNumber, r.VoucherCode, r.ListPrice, r.DiscountAmount);
    }

    private static string Describe(MoneyLogType type, MoneyLogRow r, string? itemName)
    {
        var item = itemName is null ? "" : $" · {itemName}";
        return type switch
        {
            MoneyLogType.CashTopUp => r.InvoiceNumber is null ? "Nạp ví tại quầy" : $"Nạp ví tại quầy · {r.InvoiceNumber}",
            MoneyLogType.OnlineTopUp => r.Status switch
            {
                PaymentStatus.Pending => "Nạp ví qua PayOS · chưa thanh toán",
                PaymentStatus.Failed => "Nạp ví qua PayOS · thất bại",
                _ => "Nạp ví qua PayOS",
            },
            MoneyLogType.Purchase => $"Mua {KindLabel(r.OrderKind)}{item}",
            _ => (r.Reference.EndsWith(":pt-refund") ? "Hoàn buổi PT"
                : r.Reference.EndsWith(":package-refund") ? "Hoàn gói tập"
                : r.Reference.StartsWith("course-unit:") ? "Hoàn buổi khóa học" : "Hoàn tiền") + item,
        };
    }

    private static string KindLabel(CommerceOrderKind? kind) => kind switch
    {
        CommerceOrderKind.Course => "khóa học",
        CommerceOrderKind.PtPackage => "gói PT",
        CommerceOrderKind.PtSingle => "buổi PT lẻ",
        _ => "gói tập",
    };

    internal static List<RevenueItem> GroupByItem(IEnumerable<RevenueSourceRow> rows) => rows
        .Select(r => (r.Kind, Item: r.Snapshot is null ? (Id: r.PlanId, Name: r.PlanName) : ReadSnapshotItem(r.Snapshot), r.Amount))
        .GroupBy(x => (x.Kind, Id: x.Item.Id))
        .Select(g => new RevenueItem(g.Key.Kind, g.Key.Id,
            g.Select(x => x.Item.Name).FirstOrDefault(n => n is not null) ?? DefaultName(g.Key.Kind), g.Count(), g.Sum(x => x.Amount)))
        .OrderByDescending(x => x.Revenue).ToList();

    // Orders keep the bought item as JSON ({"Id":..,"Name":..}); single PT sessions have neither.
    private static (int? Id, string? Name) ReadSnapshotItem(string snapshot)
    {
        try
        {
            var root = System.Text.Json.JsonDocument.Parse(snapshot).RootElement;
            return (root.TryGetProperty("Id", out var id) && id.TryGetInt32(out var v) ? v : null,
                root.TryGetProperty("Name", out var name) ? name.GetString() : null);
        }
        catch (System.Text.Json.JsonException) { return (null, null); }
    }

    private static string DefaultName(CommerceOrderKind kind) => kind switch
    {
        CommerceOrderKind.PtSingle => "Buổi PT lẻ",
        CommerceOrderKind.Course => "Khóa học (không rõ)",
        CommerceOrderKind.PtPackage => "Gói PT (không rõ)",
        _ => "Gói tập (không rõ)",
    };
}
