using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.Services.Membership;

public record RevenueReport(DateOnly From, DateOnly To, decimal TotalRevenue, int PaymentCount, int NewMembers,
    int NewSubscriptions, int ActiveSubscriptions, int ExpiredSubscriptions, int NewClassBookings);
// ItemId is the plan / course / PT package id; null for single PT sessions (one line for all of them).
public record RevenueItem(CommerceOrderKind Kind, int? ItemId, string Name, int Orders, decimal Revenue);
public record RevenueByItemReport(DateOnly From, DateOnly To, decimal TotalRevenue, List<RevenueItem> Items);
public record WalletReceiptReport(DateOnly From, DateOnly To, List<WalletReceiptRow> Items, int Total, int Page, int PageSize,
    decimal TotalAmount, decimal CashAmount, decimal BankTransferAmount);

public interface IMembershipReportService
{
    Task<RevenueReport> GetRevenueReportAsync(int actorId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<RevenueByItemReport> GetRevenueByItemAsync(int actorId, DateOnly from, DateOnly to, CancellationToken ct);
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
