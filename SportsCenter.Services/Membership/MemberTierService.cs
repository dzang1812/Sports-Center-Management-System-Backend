using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.Services.Membership;

public record MemberTierInfo(MemberTier Tier, string Name, decimal MinSpend);
public record MemberTierDto(int MemberId, decimal LifetimeSpend, MemberTier Tier, string TierName,
    MemberTier? NextTier, string? NextTierName, decimal? NextTierMinSpend, decimal? RemainingToNext, List<MemberTierInfo> Tiers);

// Tier = lifetime net spending (VND). Lifetime, so a member is never demoted; refunds lower the spend but an
// already-announced tier is not re-announced (one notification per tier, keyed member-tier:{id}:{tier}).
public sealed class MemberTierService(IMemberTierRepository repo, IUserRepository users, INotificationRepository notifications)
{
    public static readonly List<MemberTierInfo> Tiers =
    [
        new(MemberTier.New, "Hội viên mới", 0),
        new(MemberTier.Loyal, "Thân thiết", 1_000_000),
        new(MemberTier.Bronze, "Đồng", 3_000_000),
        new(MemberTier.Silver, "Bạc", 7_000_000),
        new(MemberTier.Gold, "Vàng", 15_000_000),
        new(MemberTier.Diamond, "Kim cương", 30_000_000),
    ];

    public static MemberTierInfo TierFor(decimal spend) => Tiers.Last(t => spend >= t.MinSpend);

    public async Task<MemberTierDto> GetAsync(int actorId, int memberId, CancellationToken ct)
    {
        var actor = await users.GetByIdAsync(actorId);
        var allowed = actor is { IsActive: true } && (actorId == memberId || actor.Role is UserRole.Manager or UserRole.Receptionist);
        if (!allowed) throw SchedulingException.Forbidden("MEMBER_TIER_FORBIDDEN", "Bạn không có quyền xem hạng hội viên này.");
        var member = await users.GetByIdAsync(memberId);
        if (member is null || member.Role != UserRole.Member) throw SchedulingException.NotFound("MEMBER_NOT_FOUND", "Không tìm thấy hội viên.");

        var spend = await repo.GetLifetimeSpendAsync(memberId, ct);
        // Catch-up for paths that don't announce upgrades themselves (e.g. older payments).
        if (await AnnounceAsync(memberId, spend, ct)) await notifications.SaveChangesAsync(ct);
        var tier = TierFor(spend);
        var next = Tiers.FirstOrDefault(t => t.MinSpend > spend);
        return new(memberId, spend, tier.Tier, tier.Name, next?.Tier, next?.Name, next?.MinSpend, next is null ? null : next.MinSpend - spend, Tiers);
    }

    // Call after money is spent, inside the purchase's unit of work; the caller's SaveChanges persists the notification.
    // `unsavedSpend`: an amount already spent but not yet saved to the database (e.g. a payment being confirmed).
    public async Task CheckUpgradeAsync(int memberId, CancellationToken ct, decimal unsavedSpend = 0) =>
        await AnnounceAsync(memberId, await repo.GetLifetimeSpendAsync(memberId, ct) + unsavedSpend, ct);

    private async Task<bool> AnnounceAsync(int memberId, decimal spend, CancellationToken ct)
    {
        var tier = TierFor(spend);
        if (tier.Tier == MemberTier.New) return false;
        var key = $"member-tier:{memberId}:{tier.Tier}";
        if (await repo.NotificationExistsAsync(key, ct)) return false;
        var next = Tiers.FirstOrDefault(t => t.MinSpend > spend);
        notifications.AddNotification(memberId, null, NotificationType.General, $"Chúc mừng bạn lên hạng {tier.Name}!",
            next is null ? "Bạn đã đạt hạng cao nhất của FitZone. Cảm ơn bạn đã luôn đồng hành!"
                : $"Chi tiêu thêm {next.MinSpend - spend:N0}đ để lên hạng {next.Name}.", key);
        return true;
    }
}
