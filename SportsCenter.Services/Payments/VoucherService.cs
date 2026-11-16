using System.Text.RegularExpressions;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Membership;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.Services.Payments;

public record VoucherRequest(string Code, string? Description, VoucherDiscountType DiscountType, decimal DiscountValue, decimal? MaxDiscount,
    decimal MinOrderAmount, CommerceOrderKind? AppliesTo, MemberTier MinTier, DateTime StartsAt, DateTime EndsAt, int? UsageLimit, int PerMemberLimit, bool IsActive, bool RequiresGrant = false);
public record VoucherDto(int Id, string Code, string Description, VoucherDiscountType DiscountType, decimal DiscountValue, decimal? MaxDiscount,
    decimal MinOrderAmount, CommerceOrderKind? AppliesTo, MemberTier MinTier, DateTime StartsAt, DateTime EndsAt, int? UsageLimit, int PerMemberLimit,
    bool IsActive, bool RequiresGrant, int UsedCount);
// GrantedAt/GrantedBy set when staff sent it to this member; Usable = can be applied at checkout right now.
public record MemberVoucherDto(VoucherDto Voucher, int RemainingForMe, bool Usable, DateTime? GrantedAt, string? GrantedBy);
public record GrantVoucherRequest(int MemberId);
public record VoucherPreviewRequest(string Code, CommerceOrderKind Kind, decimal Price, int? MemberId);
public record VoucherPreview(string Code, decimal ListPrice, decimal DiscountAmount, decimal FinalPrice, string Description);
public record AppliedVoucher(Voucher Voucher, decimal DiscountAmount);

// Vouchers lower the price of wallet purchases (course, PT single, PT package, self-practice membership).
// The order stores the discounted PaidAmount, so every refund path (which refunds from PaidAmount) stays correct.
// shortcut: a refunded order keeps its redemption (the voucher use is not given back); add a restore step if staff ask for it.
public sealed partial class VoucherService(IVoucherRepository repo, IUserRepository users, IMemberTierRepository tiers,
    IAuditLogRepository audit, INotificationRepository notifications, IAppClock clock)
{
    public const decimal MinPayable = 1_000m; // orders must stay > 0 VND (CK_Order_Refund, course allocation)

    // Staff list: managers edit from it, receptionists pick a voucher to send.
    public async Task<List<VoucherDto>> ListAsync(int actorId, CancellationToken ct)
    {
        await StaffAsync(actorId);
        var used = await repo.CountRedemptionsAsync(ct);
        return (await repo.ListAsync(ct)).Select(v => Dto(v, used.GetValueOrDefault(v.Id))).ToList();
    }

    public async Task<VoucherDto> CreateAsync(int actorId, VoucherRequest request, CancellationToken ct)
    {
        await ManagerAsync(actorId);
        var voucher = new Voucher { CreatedById = actorId, CreatedAt = clock.UtcNow };
        await ApplyRequestAsync(voucher, request, ct);
        repo.Add(voucher);
        await repo.SaveAsync(ct);
        audit.AddAudit(actorId, "Voucher.Create", nameof(Voucher), voucher.Id, request);
        await repo.SaveAsync(ct);
        return Dto(voucher, 0);
    }

    public async Task<VoucherDto> UpdateAsync(int actorId, int id, VoucherRequest request, CancellationToken ct)
    {
        await ManagerAsync(actorId);
        var voucher = await repo.GetAsync(id, ct) ?? throw SchedulingException.NotFound("VOUCHER_NOT_FOUND", "Không tìm thấy voucher.");
        var used = await repo.CountRedemptionsAsync(id, null, ct);
        if (used > 0 && voucher.Code != Normalize(request.Code))
            throw SchedulingException.Conflict("VOUCHER_CODE_LOCKED", "Voucher đã được sử dụng, không thể đổi mã.");
        await ApplyRequestAsync(voucher, request, ct);
        audit.AddAudit(actorId, "Voucher.Update", nameof(Voucher), id, request);
        await repo.SaveAsync(ct);
        return Dto(voucher, used);
    }

    // Staff sends a voucher to one member (once per voucher); the member is notified and sees it in the vouchers page.
    // A sent voucher skips the MinTier check: staff chose this member on purpose.
    public async Task<MemberVoucherDto> GrantAsync(int actorId, int voucherId, int memberId, CancellationToken ct)
    {
        var actor = await StaffAsync(actorId);
        var v = await repo.GetAsync(voucherId, ct) ?? throw SchedulingException.NotFound("VOUCHER_NOT_FOUND", "Không tìm thấy voucher.");
        if (!v.IsActive || v.EndsAt <= clock.UtcNow) throw SchedulingException.Conflict("VOUCHER_INACTIVE", "Voucher đã tắt hoặc hết hạn, không thể gửi.");
        var member = await users.GetByIdAsync(memberId);
        if (member is null || !member.IsActive || member.Role != UserRole.Member) throw SchedulingException.NotFound("MEMBER_NOT_FOUND", "Không tìm thấy hội viên đang hoạt động.");
        if (await repo.HasGrantAsync(voucherId, memberId, ct)) throw SchedulingException.Conflict("VOUCHER_ALREADY_SENT", "Hội viên đã được gửi voucher này.");
        repo.Add(new VoucherGrant { VoucherId = voucherId, MemberId = memberId, GrantedById = actorId, CreatedAt = clock.UtcNow });
        audit.AddAudit(actorId, "Voucher.Grant", nameof(Voucher), voucherId, new { memberId });
        notifications.AddNotification(memberId, actorId, NotificationType.General, $"Bạn nhận được voucher {v.Code}",
            $"{DiscountText(v)}{(v.Description.Length > 0 ? $" — {v.Description}" : "")}. Hạn dùng đến {v.EndsAt + (clock.LocalNow - clock.UtcNow):dd/MM/yyyy}. Xem tại mục Ưu đãi.",
            $"voucher-grant:{voucherId}:{memberId}");
        await repo.SaveAsync(ct);
        var used = await repo.CountRedemptionsAsync(voucherId, null, ct);
        var mine = await repo.CountRedemptionsAsync(voucherId, memberId, ct);
        return new(Dto(v, used), Math.Max(0, v.PerMemberLimit - mine), Usable(v, true, MemberTier.New, used, mine), clock.UtcNow, actor.FullName);
    }

    // Member: every voucher sent to them (even used/expired, as history) + public vouchers they can use right now.
    public async Task<List<MemberVoucherDto>> ListForMemberAsync(int memberId, CancellationToken ct)
    {
        var tier = MemberTierService.TierFor(await tiers.GetLifetimeSpendAsync(memberId, ct)).Tier;
        var used = await repo.CountRedemptionsAsync(ct);
        var mine = await repo.CountMemberRedemptionsAsync(memberId, ct);
        MemberVoucherDto Item(Voucher v, VoucherGrant? g) => new(Dto(v, used.GetValueOrDefault(v.Id)), Math.Max(0, v.PerMemberLimit - mine.GetValueOrDefault(v.Id)),
            Usable(v, g is not null, tier, used.GetValueOrDefault(v.Id), mine.GetValueOrDefault(v.Id)), g?.CreatedAt, g?.GrantedBy.FullName);
        var grants = await repo.ListGrantsAsync(memberId, ct);
        var granted = grants.Select(g => g.VoucherId).ToHashSet();
        var result = grants.Select(g => Item(g.Voucher, g)).ToList();
        result.AddRange((await repo.ListActiveAsync(clock.UtcNow, ct))
            .Where(v => !v.RequiresGrant && !granted.Contains(v.Id)).Select(v => Item(v, null)).Where(x => x.Usable));
        return result;
    }

    private bool Usable(Voucher v, bool granted, MemberTier tier, int used, int mine) =>
        v.IsActive && v.StartsAt <= clock.UtcNow && v.EndsAt > clock.UtcNow && (granted || (!v.RequiresGrant && tier >= v.MinTier))
        && (v.UsageLimit is null || used < v.UsageLimit) && mine < v.PerMemberLimit;

    // Member checks their own price; receptionist/manager checks for the member they are selling to.
    public async Task<VoucherPreview> PreviewAsync(int actorId, VoucherPreviewRequest request, CancellationToken ct)
    {
        var actor = await users.GetByIdAsync(actorId);
        if (actor is null || !actor.IsActive) throw SchedulingException.Forbidden("VOUCHER_FORBIDDEN", "Tài khoản không hợp lệ.");
        var memberId = actor.Role == UserRole.Member ? actorId
            : request.MemberId ?? throw new SchedulingException("MEMBER_REQUIRED", "Chọn hội viên để kiểm tra voucher.");
        WalletService.ValidateMoney(request.Price);
        var applied = await ResolveAsync(memberId, request.Code, request.Kind, request.Price, ct);
        return new(applied.Voucher.Code, request.Price, applied.DiscountAmount, request.Price - applied.DiscountAmount, applied.Voucher.Description);
    }

    // Purchase flow: call inside the purchase transaction after locking ScheduleResourceType.Voucher for this id,
    // so usage limits cannot be exceeded by concurrent purchases.
    public async Task<int> FindIdAsync(string code, CancellationToken ct) =>
        (await repo.FindByCodeAsync(Normalize(code), ct))?.Id
        ?? throw Invalid("VOUCHER_NOT_FOUND", "Mã voucher không tồn tại hoặc đã ngừng áp dụng.");

    public Task<AppliedVoucher> ResolveForPurchaseAsync(int memberId, string code, CommerceOrderKind kind, decimal price, CancellationToken ct) =>
        ResolveAsync(memberId, code, kind, price, ct);

    public void Redeem(AppliedVoucher applied, int memberId, CommerceOrder order, decimal listPrice) => repo.Add(new VoucherRedemption
    {
        VoucherId = applied.Voucher.Id, MemberId = memberId, OrderId = order.Id, ListPrice = listPrice,
        DiscountAmount = applied.DiscountAmount, CreatedAt = clock.UtcNow,
    });

    // Never discounts below MinPayable; percent is floored to whole VND, then capped.
    public static decimal Discount(VoucherDiscountType type, decimal value, decimal? maxDiscount, decimal price)
    {
        var raw = type == VoucherDiscountType.Percent ? decimal.Floor(price * value / 100m) : value;
        if (maxDiscount is { } cap) raw = Math.Min(raw, cap);
        return Math.Max(0, Math.Min(raw, price - MinPayable));
    }

    private async Task<AppliedVoucher> ResolveAsync(int memberId, string code, CommerceOrderKind kind, decimal price, CancellationToken ct)
    {
        var v = await repo.FindByCodeAsync(Normalize(code), ct);
        var now = clock.UtcNow;
        if (v is null || !v.IsActive) throw Invalid("VOUCHER_NOT_FOUND", "Mã voucher không tồn tại hoặc đã ngừng áp dụng.");
        if (now < v.StartsAt) throw Invalid("VOUCHER_NOT_STARTED", $"Voucher áp dụng từ {v.StartsAt + (clock.LocalNow - clock.UtcNow):dd/MM/yyyy HH:mm}.");
        if (now >= v.EndsAt) throw Invalid("VOUCHER_EXPIRED", "Voucher đã hết hạn.");
        if (v.AppliesTo is { } only && only != kind) throw Invalid("VOUCHER_KIND_MISMATCH", $"Voucher chỉ áp dụng cho {KindName(only)}.");
        if (price < v.MinOrderAmount) throw Invalid("VOUCHER_MIN_ORDER", $"Đơn tối thiểu {v.MinOrderAmount:N0}đ để dùng voucher này.");
        var granted = await repo.HasGrantAsync(v.Id, memberId, ct);
        if (v.RequiresGrant && !granted) throw Invalid("VOUCHER_NOT_GRANTED", "Voucher này chỉ dành cho hội viên được tặng.");
        var tier = MemberTierService.TierFor(await tiers.GetLifetimeSpendAsync(memberId, ct));
        if (!granted && tier.Tier < v.MinTier)
            throw Invalid("VOUCHER_TIER", $"Voucher dành cho hạng {MemberTierService.Tiers.First(t => t.Tier == v.MinTier).Name} trở lên.");
        if (v.UsageLimit is { } limit && await repo.CountRedemptionsAsync(v.Id, null, ct) >= limit) throw Invalid("VOUCHER_SOLD_OUT", "Voucher đã hết lượt sử dụng.");
        if (await repo.CountRedemptionsAsync(v.Id, memberId, ct) >= v.PerMemberLimit) throw Invalid("VOUCHER_USED", "Bạn đã dùng hết lượt của voucher này.");
        var discount = Discount(v.DiscountType, v.DiscountValue, v.MaxDiscount, price);
        if (discount <= 0) throw Invalid("VOUCHER_NO_DISCOUNT", "Voucher không giảm được cho đơn này.");
        return new(v, discount);
    }

    private async Task ApplyRequestAsync(Voucher v, VoucherRequest r, CancellationToken ct)
    {
        var code = Normalize(r.Code);
        if (!CodePattern().IsMatch(code)) throw Invalid("VOUCHER_CODE_INVALID", "Mã voucher 3–30 ký tự, chỉ gồm chữ, số, - và _.");
        if (await repo.CodeExistsAsync(code, v.Id == 0 ? null : v.Id, ct)) throw SchedulingException.Conflict("VOUCHER_CODE_EXISTS", "Mã voucher đã tồn tại.");
        if (r.DiscountValue != decimal.Truncate(r.DiscountValue) || r.DiscountValue <= 0
            || (r.DiscountType == VoucherDiscountType.Percent && r.DiscountValue > 100) || r.DiscountValue > 1_000_000_000m)
            throw Invalid("VOUCHER_VALUE_INVALID", "Giá trị giảm phải là số nguyên dương (phần trăm tối đa 100).");
        if (r.MaxDiscount is { } cap && (cap <= 0 || cap != decimal.Truncate(cap))) throw Invalid("VOUCHER_VALUE_INVALID", "Mức giảm tối đa phải là số tiền dương.");
        if (r.MinOrderAmount < 0 || r.MinOrderAmount != decimal.Truncate(r.MinOrderAmount)) throw Invalid("VOUCHER_VALUE_INVALID", "Giá trị đơn tối thiểu không hợp lệ.");
        if (r.EndsAt <= r.StartsAt) throw Invalid("VOUCHER_DATES_INVALID", "Thời gian kết thúc phải sau thời gian bắt đầu.");
        if (r.UsageLimit is < 1 || r.PerMemberLimit < 1) throw Invalid("VOUCHER_LIMIT_INVALID", "Giới hạn lượt dùng phải từ 1 trở lên.");
        var description = r.Description?.Trim() ?? "";
        if (description.Length > 300) throw Invalid("VOUCHER_DESCRIPTION_INVALID", "Mô tả tối đa 300 ký tự.");
        v.Code = code; v.Description = description; v.DiscountType = r.DiscountType; v.DiscountValue = r.DiscountValue;
        v.MaxDiscount = r.DiscountType == VoucherDiscountType.Percent ? r.MaxDiscount : null; v.MinOrderAmount = r.MinOrderAmount;
        v.AppliesTo = r.AppliesTo; v.MinTier = r.MinTier; v.StartsAt = r.StartsAt.ToUniversalTime(); v.EndsAt = r.EndsAt.ToUniversalTime();
        v.UsageLimit = r.UsageLimit; v.PerMemberLimit = r.PerMemberLimit; v.IsActive = r.IsActive; v.RequiresGrant = r.RequiresGrant;
    }

    private async Task<User> StaffAsync(int actorId)
    {
        var actor = await users.GetByIdAsync(actorId);
        if (actor is null || !actor.IsActive || actor.Role is not (UserRole.Manager or UserRole.Receptionist))
            throw SchedulingException.Forbidden("VOUCHER_FORBIDDEN", "Chỉ quản lý hoặc lễ tân được xem và gửi voucher.");
        return actor;
    }

    private async Task ManagerAsync(int actorId)
    {
        var actor = await users.GetByIdAsync(actorId);
        if (actor is null || !actor.IsActive || actor.Role != UserRole.Manager)
            throw SchedulingException.Forbidden("VOUCHER_FORBIDDEN", "Chỉ quản lý được tạo và chỉnh sửa voucher.");
    }

    private static VoucherDto Dto(Voucher v, int used) => new(v.Id, v.Code, v.Description, v.DiscountType, v.DiscountValue, v.MaxDiscount,
        v.MinOrderAmount, v.AppliesTo, v.MinTier, v.StartsAt, v.EndsAt, v.UsageLimit, v.PerMemberLimit, v.IsActive, v.RequiresGrant, used);

    private static string DiscountText(Voucher v) => v.DiscountType == VoucherDiscountType.Percent
        ? $"Giảm {v.DiscountValue:0}%" + (v.MaxDiscount is { } cap ? $" (tối đa {cap:N0}đ)" : "") : $"Giảm {v.DiscountValue:N0}đ";

    public static string Normalize(string? code) => (code ?? "").Trim().ToUpperInvariant();

    private static string KindName(CommerceOrderKind kind) => kind switch
    {
        CommerceOrderKind.Course => "khóa học", CommerceOrderKind.PtSingle => "PT buổi lẻ",
        CommerceOrderKind.PtPackage => "gói PT", _ => "gói tự tập",
    };

    private static SchedulingException Invalid(string code, string message) => new(code, message);

    [GeneratedRegex("^[A-Z0-9_-]{3,30}$")]
    private static partial Regex CodePattern();
}
