using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.Services.Payments;

public record CashTopUpRequest(int MemberId, decimal Amount, string ReceiptReference);
public record OnlineTopUpRequest(decimal Amount);
public record WalletDto(int MemberId, decimal Balance, decimal LedgerBalance);
public record WalletEntryDto(int Id, decimal Amount, decimal BalanceAfter, string Kind, string Reference,
    int? OrderId, int? PaymentId, int ActorId, DateTime CreatedAt);

public sealed class WalletService(ICommerceRepository repo, ISchedulingTransaction tx, IScheduleUserRepository users,
    IAuditLogRepository audit, INotificationRepository notifications, IAppClock clock)
{
    public async Task<int> TargetAsync(int actorId, int? requested, bool write, CancellationToken ct)
    {
        var actor = await users.GetUserAsync(actorId, ct);
        if (actor is null || !actor.IsActive) throw SchedulingException.Forbidden("ACTOR_INACTIVE", "Tài khoản không hoạt động.");
        int target;
        if (actor.Role == UserRole.Member && (requested is null || requested == actorId)) target = actorId;
        else if ((actor.Role == UserRole.Receptionist || !write && actor.Role == UserRole.Manager) && requested is > 0) target = requested.Value;
        else throw SchedulingException.Forbidden("WALLET_ACCESS_DENIED", "Không có quyền thao tác tài khoản được chọn.");
        if (!await users.IsActiveInRoleAsync(target, UserRole.Member, ct)) throw SchedulingException.Conflict("MEMBER_INACTIVE", "Hội viên không hoạt động.");
        return target;
    }

    public async Task<WalletDto> GetAsync(int actorId, int? memberId, CancellationToken ct)
    {
        var id = await TargetAsync(actorId, memberId, false, ct);
        // Both values are read in one statement/snapshot, including an account with no ledger yet.
        var row = await repo.GetWalletBalanceAsync(id, ct);
        return new(row.MemberId, row.Balance, row.LedgerBalance);
    }

    public async Task<PagedResponse<WalletEntryDto>> LedgerAsync(int actorId, int? memberId, int page, CancellationToken ct)
    {
        var id = await TargetAsync(actorId, memberId, false, ct);
        page = Math.Clamp(page, 1, 1000000);
        var (items, total) = await repo.ListWalletEntriesAsync(id, page, ct);
        return new(items.Select(x => new WalletEntryDto(x.Id, x.Amount, x.BalanceAfter, x.Kind, x.Reference,
            x.OrderId, x.PaymentId, x.ActorId, x.CreatedAt)).ToList(), total, page, 20);
    }

    public Task<int> CashAsync(int actorId, CashTopUpRequest request, string key, CancellationToken ct) =>
        ExecuteAsync(actorId, "cash-top-up", key, request, async token =>
        {
            await users.EnsureActiveActorAsync(actorId, UserRole.Receptionist, token);
            var member = await TargetAsync(actorId, request.MemberId, true, token);
            ValidateMoney(request.Amount);
            if (string.IsNullOrWhiteSpace(request.ReceiptReference) || request.ReceiptReference.Trim().Length > 100)
                throw new SchedulingException("RECEIPT_REQUIRED", "Cần mã biên nhận duy nhất, tối đa 100 ký tự.");
            await tx.LockAsync([new(ScheduleResourceType.Member, member)], token);
            var reference = "cash:" + request.ReceiptReference.Trim();
            if (await repo.PaymentReferenceExistsAsync(reference, token))
                throw SchedulingException.Conflict("RECEIPT_EXISTS", "Biên nhận đã ghi nhận; không nạp lần hai.");
            var payment = new Payment { MemberId = member, ReceivedById = actorId, Amount = request.Amount,
                Method = PaymentMethod.Cash, Purpose = PaymentPurpose.WalletTopUp, Status = PaymentStatus.Paid,
                ProviderReference = reference, PaidAt = clock.UtcNow };
            repo.Add(payment); await tx.SaveChangesAsync(token);
            payment.PayOsOrderCode = payment.Id;
            payment.InvoiceNumber = $"WAL-{payment.Id}";
            await MoveAsync(member, actorId, request.Amount, "CashTopUp", $"payment:{payment.Id}:credit", null, payment.Id, token);
            return payment.Id;
        }, ct);

    // Caller must hold the shared Member advisory lock and an open scheduling transaction.
    public async Task MoveAsync(int memberId, int actorId, decimal signedAmount, string kind, string reference,
        int? orderId, int? paymentId, CancellationToken ct)
    {
        ValidateMoney(Math.Abs(signedAmount));
        if (await repo.WalletReferenceExistsAsync(reference, ct))
            throw SchedulingException.Conflict("WALLET_REFERENCE_EXISTS", "Giao dịch đã được ghi nhận.");
        var wallet = await repo.FindWalletAsync(memberId, ct);
        if (wallet is null) { wallet = new MemberWallet { MemberId = memberId }; repo.Add(wallet); }
        if (wallet.Balance + signedAmount < 0) throw SchedulingException.Conflict("INSUFFICIENT_BALANCE", "Số dư không đủ.");
        if (wallet.Balance + signedAmount > 9999999999m) throw SchedulingException.Conflict("WALLET_LIMIT", "Số dư vượt giới hạn.");
        wallet.Balance += signedAmount;
        repo.Add(new WalletEntry { MemberId = memberId, ActorId = actorId, Amount = signedAmount,
            BalanceAfter = wallet.Balance, Kind = kind, Reference = reference, OrderId = orderId, PaymentId = paymentId, CreatedAt = clock.UtcNow });
        audit.AddAudit(actorId, "Wallet." + kind, nameof(MemberWallet), memberId, new { signedAmount, reference, orderId, paymentId });
        notifications.AddNotification(memberId, actorId, NotificationType.General, "Biến động số dư",
            $"{kind}: {signedAmount:N0} VND. Số dư: {wallet.Balance:N0} VND.", "wallet:" + reference);
    }

    public async Task<T> ExecuteAsync<T>(int actorId, string operation, string key, object payload,
        Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        var actor = await users.GetUserAsync(actorId, ct);
        if (actor is null || !actor.IsActive) throw SchedulingException.Forbidden("ACTOR_INACTIVE", "Tài khoản không hoạt động.");
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100) throw new SchedulingException("IDEMPOTENCY_KEY_REQUIRED", "Cần Idempotency-Key (1..100 ký tự).");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))));
        return await tx.ExecuteAsync(async token =>
        {
            await tx.LockAsync([new(ScheduleResourceType.CommerceRequest, actorId)], token);
            var old = await repo.FindRequestAsync(actorId, operation, key, token);
            if (old is not null)
            {
                if (old.PayloadHash != hash) throw SchedulingException.Conflict("IDEMPOTENCY_PAYLOAD_MISMATCH", "Key đã dùng với dữ liệu khác.");
                return JsonSerializer.Deserialize<T>(old.ResultJson)!;
            }
            var result = await action(token);
            repo.Add(new CommerceRequest { ActorId = actorId, Operation = operation, Key = key, PayloadHash = hash, ResultJson = JsonSerializer.Serialize(result) });
            return result;
        }, ct);
    }

    public static void ValidateMoney(decimal amount)
    {
        if (amount <= 0 || amount != decimal.Truncate(amount) || amount > 1000000000m)
            throw new SchedulingException("INVALID_VND_AMOUNT", "Số tiền phải là VND nguyên dương, tối đa 1 tỷ.");
    }
}
