using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

public interface IVoucherRepository
{
    Task<List<Voucher>> ListAsync(CancellationToken ct);
    Task<List<Voucher>> ListActiveAsync(DateTime utcNow, CancellationToken ct);
    Task<Voucher?> GetAsync(int id, CancellationToken ct);
    Task<Voucher?> FindByCodeAsync(string code, CancellationToken ct);
    Task<bool> CodeExistsAsync(string code, int? exceptId, CancellationToken ct);
    Task<Dictionary<int, int>> CountRedemptionsAsync(CancellationToken ct);
    Task<int> CountRedemptionsAsync(int voucherId, int? memberId, CancellationToken ct);
    Task<Dictionary<int, int>> CountMemberRedemptionsAsync(int memberId, CancellationToken ct);
    Task<bool> HasGrantAsync(int voucherId, int memberId, CancellationToken ct);
    Task<List<VoucherGrant>> ListGrantsAsync(int memberId, CancellationToken ct);
    void Add(VoucherGrant grant);
    void Add(Voucher voucher);
    void Add(VoucherRedemption redemption);
    Task SaveAsync(CancellationToken ct);
}

public sealed class VoucherRepository(AppDbContext db) : IVoucherRepository
{
    public Task<List<Voucher>> ListAsync(CancellationToken ct) =>
        db.Vouchers.AsNoTracking().OrderByDescending(v => v.CreatedAt).ToListAsync(ct);

    public Task<List<Voucher>> ListActiveAsync(DateTime utcNow, CancellationToken ct) =>
        db.Vouchers.AsNoTracking().Where(v => v.IsActive && v.StartsAt <= utcNow && v.EndsAt > utcNow)
            .OrderBy(v => v.EndsAt).ToListAsync(ct);

    public Task<Voucher?> GetAsync(int id, CancellationToken ct) => db.Vouchers.FirstOrDefaultAsync(v => v.Id == id, ct);

    public Task<Voucher?> FindByCodeAsync(string code, CancellationToken ct) =>
        db.Vouchers.AsNoTracking().FirstOrDefaultAsync(v => v.Code == code, ct);

    public Task<bool> CodeExistsAsync(string code, int? exceptId, CancellationToken ct) =>
        db.Vouchers.AnyAsync(v => v.Code == code && v.Id != exceptId, ct);

    public Task<Dictionary<int, int>> CountRedemptionsAsync(CancellationToken ct) =>
        db.VoucherRedemptions.GroupBy(r => r.VoucherId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    public Task<int> CountRedemptionsAsync(int voucherId, int? memberId, CancellationToken ct) =>
        db.VoucherRedemptions.CountAsync(r => r.VoucherId == voucherId && (memberId == null || r.MemberId == memberId), ct);

    public Task<Dictionary<int, int>> CountMemberRedemptionsAsync(int memberId, CancellationToken ct) =>
        db.VoucherRedemptions.Where(r => r.MemberId == memberId).GroupBy(r => r.VoucherId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    public Task<bool> HasGrantAsync(int voucherId, int memberId, CancellationToken ct) =>
        db.VoucherGrants.AnyAsync(g => g.VoucherId == voucherId && g.MemberId == memberId, ct);

    public Task<List<VoucherGrant>> ListGrantsAsync(int memberId, CancellationToken ct) =>
        db.VoucherGrants.AsNoTracking().Include(g => g.Voucher).Include(g => g.GrantedBy).Where(g => g.MemberId == memberId)
            .OrderByDescending(g => g.CreatedAt).ToListAsync(ct);

    public void Add(VoucherGrant grant) => db.VoucherGrants.Add(grant);
    public void Add(Voucher voucher) => db.Vouchers.Add(voucher);
    public void Add(VoucherRedemption redemption) => db.VoucherRedemptions.Add(redemption);
    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
