using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.Services.Membership;

public record RevenueReport(DateOnly From, DateOnly To, decimal TotalRevenue, int PaymentCount, int NewMembers,
    int NewSubscriptions, int ActiveSubscriptions, int ExpiredSubscriptions, int NewClassBookings);

public interface IMembershipReportService
{
    Task<RevenueReport> GetRevenueReportAsync(int actorId, DateOnly from, DateOnly to, CancellationToken ct);
}

public sealed class MembershipReportService(IMembershipRepository repo, IUserRepository users, IAppClock clock) : IMembershipReportService
{
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
}
