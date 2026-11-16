using System.ComponentModel.DataAnnotations;
using SportsCenter.Services.Payments;
using Microsoft.Extensions.Options;
using PayOS;
using PayOS.Models.V2.PaymentRequests;
using PayOS.Models.Webhooks;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Email;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.Services.Membership;

public record SubscribeRequest([param: Required, Range(1, int.MaxValue)] int PlanId, int? MemberId,
    PaymentMethod? Method = null);

public record SubscriptionDto(int Id, int MemberId, int PlanId, string PlanName, DateOnly StartDate, DateOnly EndDate,
    SubscriptionStatus Status, int RemainingPtSessions, bool IncludesGymAccess, string PlanType, int TotalPtSessions,
    int RemainingDays, bool IncludesClasses, int? SportId, string? SportName,
    IReadOnlyList<PlanSportDto> AllowedSports, PaymentStatus? PaymentStatus, int? PurchaseOrderId = null);

// Dữ liệu gồm cả đơn chờ trả tiền và biên nhận đã thanh toán; giao diện phân biệt bằng trạng thái.
public record PaymentDto(int Id, int MemberId, string MemberName, int? SubscriptionId, string? PlanName,
    decimal Amount, PaymentMethod Method, PaymentStatus Status, string? InvoiceNumber, DateTime PaidAt,
    string? CheckoutUrl, string? QrCode, string? ReceivedByName, PaymentPurpose Purpose = PaymentPurpose.LegacySubscription);

public record PayOsSyncSummary(int Checked, int MarkedFailed, int CancelledOnPayOs, int StillPending, int Errors);

public interface ISubscriptionService
{
    Task<PaymentDto> CreateWalletTopUpAsync(int actorId, OnlineTopUpRequest request, string key, CancellationToken ct);
    Task<PaymentDto> GetCounterTopUpCheckoutAsync(int actorId, int paymentId, CancellationToken ct);
    Task<PaymentDto> SubscribeAsync(int actorId, SubscribeRequest request, CancellationToken ct);
    Task<List<SubscriptionDto>> ListForMemberAsync(int actorId, int? memberId, CancellationToken ct);
    Task<PaymentDto> GetPaymentAsync(int actorId, int paymentId, CancellationToken ct);
    Task<PaymentDto> ConfirmCounterPaymentAsync(int actorId, int paymentId, CancellationToken ct);
    Task<List<PaymentDto>> ListPendingCounterPaymentsAsync(int actorId, CancellationToken ct);
    Task HandleWebhookAsync(Webhook webhook, CancellationToken ct);
    Task<string> ConfirmWebhookUrlAsync(int actorId, string webhookUrl, CancellationToken ct);
    Task<PaymentDto> SyncPayOsStatusAsync(int actorId, int paymentId, CancellationToken ct);
    Task<PayOsSyncSummary> SyncPendingPayOsAsync(int actorId, CancellationToken ct);
    Task<PayOsSyncSummary> SyncAllPendingPayOsAsync(CancellationToken ct);
}

public sealed class SubscriptionService(IMembershipRepository repo, IUserRepository users, IAppClock clock,
    ISchedulingTransaction transaction, PayOSClient payOs, IOptions<MailOptions> mailOptions, WalletService wallet, MemberTierService tiers) : ISubscriptionService
{
    // Lấy mã thanh toán chuyển khoản cho buổi tập riêng tại quầy.
    // Nghiệp vụ: Giao dịch phải thuộc đúng lễ tân, là nạp ví chuyển khoản và không gắn với gói theo luồng thanh toán cũ.
    public async Task<PaymentDto> GetCounterTopUpCheckoutAsync(int actorId, int paymentId, CancellationToken ct)
    {
        await MembershipRules.EnsureActiveRoleAsync(users, actorId, UserRole.Receptionist);
        var payment = await repo.GetPaymentWithDetailsAsync(paymentId, ct)
            ?? throw SchedulingException.NotFound("PAYMENT_NOT_FOUND", "Không tìm thấy giao dịch.");
        if (payment.ReceivedById != actorId || payment.Purpose != PaymentPurpose.WalletTopUp ||
            payment.Method != PaymentMethod.BankTransfer || payment.SubscriptionId != null)
            throw SchedulingException.Forbidden("PAYMENT_NOT_OWNED", "Giao dịch không thuộc yêu cầu thanh toán PT của thu ngân này.");
        return await AttachCheckoutLinkAsync(paymentId, ct);
    }

    // Tạo yêu cầu nạp ví trực tuyến.
    // Nghiệp vụ: Dùng khóa chống trùng và khóa hội viên để tạo giao dịch chờ; chỉ xác nhận tiền thật mới cộng ví.
    public async Task<PaymentDto> CreateWalletTopUpAsync(int actorId, OnlineTopUpRequest request, string key, CancellationToken ct)
    {
        var paymentId = await wallet.ExecuteAsync(actorId, "online-top-up", key, request, async token =>
        {
            var member = await wallet.TargetAsync(actorId, null, true, token);
            WalletService.ValidateMoney(request.Amount);
            await transaction.LockAsync([new(ScheduleResourceType.Member, member)], token);
            var payment = new Payment { MemberId = member, Amount = request.Amount, Purpose = PaymentPurpose.WalletTopUp, Method = PaymentMethod.BankTransfer };
            await repo.AddPaymentAsync(payment, token); await transaction.SaveChangesAsync(token);
            payment.PayOsOrderCode = payment.Id;
            return payment.Id;
        }, ct);
        return await AttachCheckoutLinkAsync(paymentId, ct);
    }

    private async Task<PaymentDto> AttachCheckoutLinkAsync(int paymentId, CancellationToken ct)
    {
        var pending = await repo.GetPaymentWithDetailsAsync(paymentId, ct)
            ?? throw SchedulingException.NotFound("PAYMENT_NOT_FOUND", "Không tìm thấy giao dịch.");
        if (pending.Status != PaymentStatus.Pending || pending.CheckoutUrl != null)
            return ToPaymentDto(pending, pending.Member, pending.Subscription?.Plan.Name);
        // Không giữ giao dịch cơ sở dữ liệu khi gọi nhà cung cấp; đơn chờ đã lưu được dùng lại khi gửi lại yêu cầu.
        var link = await CreatePayOsLinkAsync(pending, ct);
        return await transaction.ExecuteAsync(async token =>
        {
            await transaction.LockAsync([new(ScheduleResourceType.Member, pending.MemberId)], token);
            pending.CheckoutUrl = link.CheckoutUrl; pending.QrCode = link.QrCode; pending.PayOsPaymentLinkId = link.PaymentLinkId;
            return ToPaymentDto(pending, pending.Member, pending.Subscription?.Plan.Name);
        }, ct);
    }

    // Đăng ký hoặc gia hạn gói theo luồng thanh toán cũ.
    // Nghiệp vụ: Dùng lại đơn chờ cùng phương thức khi gửi lại; khác phương thức thì báo xung đột, không tự thay đơn.
    // Gói tập riêng trả ví dùng đường dẫn riêng; gói mới chờ thanh toán, chỉ có quyền sau khi xác nhận tiền hợp lệ.
    public async Task<PaymentDto> SubscribeAsync(int actorId, SubscribeRequest request, CancellationToken ct)
    {
        var (actor, member) = await ResolveSubscribeTargetAsync(actorId, request.MemberId);
        var method = request.Method ?? PaymentMethod.BankTransfer;
        if (!Enum.IsDefined(method))
            throw new SchedulingException("PAYMENT_METHOD_INVALID", "Phương thức thanh toán không hợp lệ.");
        if (method != PaymentMethod.BankTransfer && actor.Role != UserRole.Receptionist)
            throw SchedulingException.Forbidden("COUNTER_PAYMENT_REQUIRES_RECEPTIONIST",
                "Chỉ lễ tân được tạo thanh toán tiền mặt hoặc thẻ tại quầy.");

        // Dùng lại đơn trực tuyến đang chờ của gói; nếu liên kết đã hủy hoặc hết hạn thì đóng đơn cũ trước.
        var previous = await repo.GetPendingSubscriptionPaymentAsync(member.Id, request.PlanId, ct);
        if (previous is { Method: PaymentMethod.BankTransfer, CheckoutUrl: not null })
            try { await SyncWithPayOsAsync(actorId, previous.Id, cancelStale: false, ct); }
            catch (SchedulingException) { } // Không kết nối được cổng thanh toán thì giữ đơn cũ.

        var draft = await transaction.ExecuteAsync(async token =>
        {
            await transaction.LockAsync([new(ScheduleResourceType.Member, member.Id)], token);
            var plan = await repo.GetPlanAsync(request.PlanId, token)
                ?? throw SchedulingException.NotFound("PLAN_NOT_FOUND", "Không tìm thấy gói thành viên.");
            if (plan.WalletPtPackage) throw SchedulingException.Conflict("USE_WALLET_PT_PACKAGE", "Gói PT mới thanh toán bằng số dư; chọn mua gói PT trong màn số dư.");
            if (!plan.IsActive)
                throw SchedulingException.Conflict("PLAN_INACTIVE", "Gói thành viên đã ngừng bán.");
            if (plan.SportId.HasValue && plan.Sport?.Status != SportStatus.Active &&
                (plan.IncludesClasses || plan.PtSessionCredits > 0))
                throw SchedulingException.Conflict("PLAN_SPORT_INACTIVE", "Bộ môn của gói đã ngừng hoạt động.");
            var pending = await repo.GetPendingSubscriptionPaymentAsync(member.Id, plan.Id, token);
            if (pending != null)
            {
                if (pending.Method != method) throw SchedulingException.Conflict("SUBSCRIPTION_PAYMENT_PENDING", "Đã có thanh toán chờ với phương thức khác.");
                return ToPaymentDto(pending, member, plan.Name);
            }

            var today = DateOnly.FromDateTime(clock.LocalNow);
            var latest = await repo.GetLatestSubscriptionAsync(member.Id, plan.Id, token);
            var startDate = latest is not null && latest.EndDate >= today ? latest.EndDate.AddDays(1) : today;
            var subscription = new MemberSubscription
            {
                MemberId = member.Id,
                PlanId = plan.Id,
                StartDate = startDate,
                EndDate = CalculateEndDate(startDate, plan),
                Status = SubscriptionStatus.Pending,
                RemainingPtSessions = plan.PtSessionCredits,
                IncludesGymAccess = plan.IncludesGymAccess,
            };
            await repo.AddSubscriptionAsync(subscription, token);
            await transaction.SaveChangesAsync(token);

            var payment = new Payment
            {
                MemberId = member.Id,
                SubscriptionId = subscription.Id,
                ReceivedById = actor.Role == UserRole.Receptionist ? actorId : null,
                Amount = plan.Price,
                Method = method,
            };
            await repo.AddPaymentAsync(payment, token);
            await transaction.SaveChangesAsync(token);

            payment.PayOsOrderCode = payment.Id;

            await repo.AddAuditAsync(actorId, "Subscription.Create", nameof(MemberSubscription), subscription.Id,
                new { memberId = member.Id, planId = plan.Id, subscription.StartDate, subscription.EndDate, payment.Amount }, token);
            return ToPaymentDto(payment, member, plan.Name);
        }, ct);
        return draft.Method == PaymentMethod.BankTransfer && draft.Status == PaymentStatus.Pending && draft.CheckoutUrl == null
            ? await AttachCheckoutLinkAsync(draft.Id, ct) : draft;
    }

    // Xác nhận tiền đã thu tại quầy.
    // Nghiệp vụ: Chỉ lễ tân có tài khoản hoạt động; không xác nhận chuyển khoản bằng tay, khóa hội viên và không xử lý đơn đã trả tiền lần hai.
    public async Task<PaymentDto> ConfirmCounterPaymentAsync(int actorId, int paymentId, CancellationToken ct)
    {
        await MembershipRules.EnsureActiveRoleAsync(users, actorId, UserRole.Receptionist);
        var key = await repo.GetPaymentKeyAsync(paymentId, ct)
            ?? throw SchedulingException.NotFound("PAYMENT_NOT_FOUND", "Không tìm thấy giao dịch thanh toán.");

        return await transaction.ExecuteAsync(async token =>
        {
            await transaction.LockAsync([new(ScheduleResourceType.Member, key.MemberId)], token);
            var payment = await repo.GetPaymentWithDetailsAsync(paymentId, token)
                ?? throw SchedulingException.NotFound("PAYMENT_NOT_FOUND", "Không tìm thấy giao dịch thanh toán.");
            if (payment.Method == PaymentMethod.BankTransfer)
                throw SchedulingException.Conflict("PAYMENT_REQUIRES_GATEWAY",
                    "Thanh toán chuyển khoản chỉ được xác nhận qua PayOS.");
            if (payment.Status == PaymentStatus.Paid)
                return ToPaymentDto(payment, payment.Member, payment.Subscription?.Plan.Name);
            if (payment.Status != PaymentStatus.Pending)
                throw SchedulingException.Conflict("PAYMENT_NOT_PENDING",
                    "Giao dịch không còn ở trạng thái chờ xác nhận.");

            await ConfirmPaymentAsync(payment, actorId, $"counter:{actorId}", token);
            await transaction.SaveChangesAsync(token);
            return ToPaymentDto(payment, payment.Member, payment.Subscription?.Plan.Name);
        }, ct);
    }

    // Lấy giao dịch tại quầy đang chờ.
    // Nghiệp vụ: Kiểm tra lễ tân đang hoạt động rồi trả danh sách để đối chiếu trước khi xác nhận thu tiền.
    public async Task<List<PaymentDto>> ListPendingCounterPaymentsAsync(int actorId, CancellationToken ct)
    {
        await MembershipRules.EnsureActiveRoleAsync(users, actorId, UserRole.Receptionist);
        var payments = await repo.ListPendingCounterPaymentsAsync(ct);
        return payments.Select(p => ToPaymentDto(p, p.Member, p.Subscription?.Plan.Name)).ToList();
    }

    // Đọc gói đã mua, hạn và quyền còn lại.
    // Nghiệp vụ: Xác định đúng hội viên theo quyền; ghép trạng thái thanh toán và thông tin gói theo ngày Việt Nam.
    public async Task<List<SubscriptionDto>> ListForMemberAsync(int actorId, int? memberId, CancellationToken ct)
    {
        var targetId = await ResolveViewTargetAsync(actorId, memberId);
        var subscriptions = await repo.ListSubscriptionsAsync(targetId, ct);
        var paymentStatuses = await repo.GetLatestPaymentStatusesAsync(subscriptions.Select(x => x.Id).ToList(), ct);
        var sports = await repo.ListSportsAsync(ct);
        var today = DateOnly.FromDateTime(clock.LocalNow);
        return subscriptions.Select(x => ToSubscriptionDto(x, sports, today,
            paymentStatuses.TryGetValue(x.Id, out var status) ? status : null)).ToList();
    }

    // Đọc chi tiết giao dịch theo quyền.
    // Nghiệp vụ: Hội viên chỉ xem giao dịch của mình; lễ tân và quản lý xem theo quyền, nhận trạng thái cùng thông tin thanh toán.
    public async Task<PaymentDto> GetPaymentAsync(int actorId, int paymentId, CancellationToken ct)
    {
        var actor = await users.GetByIdAsync(actorId)
            ?? throw SchedulingException.Forbidden("ACTOR_INACTIVE", "Tài khoản không hợp lệ hoặc đã bị khóa.");
        var payment = await repo.GetPaymentWithDetailsAsync(paymentId, ct)
            ?? throw SchedulingException.NotFound("PAYMENT_NOT_FOUND", "Không tìm thấy giao dịch thanh toán.");
        if (actor.Role == UserRole.Member && payment.MemberId != actorId)
            throw SchedulingException.Forbidden("PAYMENT_NOT_OWNED", "Không thể xem giao dịch của hội viên khác.");
        if (actor.Role is not (UserRole.Member or UserRole.Receptionist or UserRole.Manager))
            throw SchedulingException.Forbidden("ROLE_NOT_ALLOWED", "Vai trò không được xem giao dịch thanh toán.");
        return ToPaymentDto(payment, payment.Member, payment.Subscription?.Plan.Name);
    }

    // Cổng thanh toán gọi không cần đăng nhập; xác minh chữ ký để xác thực thông báo.
    // Xử lý thông báo xác nhận từ cổng thanh toán.
    // Nghiệp vụ: Xác minh chữ ký, số tiền, tiền tệ, đơn hàng, liên kết và mã tham chiếu; khóa hội viên, chỉ xử lý giao dịch đang chờ.
    // Thành công mới ghi nhận thanh toán; thông báo gửi lặp không cộng ví hoặc kích hoạt gói lần hai.
    public async Task HandleWebhookAsync(Webhook webhook, CancellationToken ct)
    {
        WebhookData data;
        try { data = await payOs.Webhooks.VerifyAsync(webhook); }
        catch (Exception) { throw new SchedulingException("PAYOS_INVALID_SIGNATURE", "Chữ ký webhook không hợp lệ."); }

        var key = await repo.GetPaymentKeyByOrderCodeAsync(data.OrderCode, ct);
        if (key is null) return;
        await transaction.ExecuteAsync(async token =>
        {
            await transaction.LockAsync([new(ScheduleResourceType.Member, key.MemberId)], token);
            var payment = await repo.GetPaymentByOrderCodeAsync(data.OrderCode, token);
            if (payment is null || payment.Status != PaymentStatus.Pending) return 0;

            if (webhook.Success != (data.Code == "00")) throw new SchedulingException("PAYMENT_VERIFICATION_MISMATCH", "Trạng thái webhook không khớp dữ liệu đã ký.");
            if (webhook.Success)
            {
                if (data.Amount != payment.Amount || data.Currency != "VND" || data.OrderCode != payment.PayOsOrderCode || string.IsNullOrWhiteSpace(data.Reference) ||
                    payment.PayOsPaymentLinkId != null && data.PaymentLinkId != payment.PayOsPaymentLinkId)
                    throw new SchedulingException("PAYMENT_VERIFICATION_MISMATCH", "Số tiền/tiền tệ/order/reference không khớp.");
                payment.ProviderReference = "payos:" + data.Reference;
                await ConfirmPaymentAsync(payment, payment.MemberId, data.Reference, token);
            }
            else await MarkFailedAsync(payment, payment.MemberId, new { payment.PayOsOrderCode }, token);
            return 0;
        }, ct);
    }

    private async Task MarkFailedAsync(Payment payment, int actorId, object details, CancellationToken ct)
    {
        payment.Status = PaymentStatus.Failed;
        if (payment.Subscription is not null) payment.Subscription.Status = SubscriptionStatus.Cancelled;
        await repo.AddAuditAsync(actorId, "Payment.Failed", nameof(Payment), payment.Id, details, ct);
    }

    // Liên kết thanh toán hết hạn sau thời gian này; tác vụ đồng bộ cũng hủy các liên kết cũ chưa có mốc hết hạn.
    private static readonly TimeSpan PayOsLinkLifetime = TimeSpan.FromMinutes(15);

    private enum SyncOutcome { Skipped, MarkedFailed, CancelledOnPayOs, StillPending }

    // Cổng chỉ chủ động báo giao dịch thành công; trạng thái khách hủy hoặc hết hạn cần hỏi lại cổng.
    // Đối chiếu giao dịch với nhà cung cấp.
    // Nghiệp vụ: Kiểm tra quyền xem trước khi hỏi cổng thanh toán; cập nhật trạng thái hủy hoặc hết hạn theo phản hồi.
    public async Task<PaymentDto> SyncPayOsStatusAsync(int actorId, int paymentId, CancellationToken ct)
    {
        // Dùng cùng quyền xem chi tiết giao dịch; đọc không theo dõi để lần đọc cuối nhận trạng thái đã được lưu.
        var actor = await users.GetByIdAsync(actorId)
            ?? throw SchedulingException.Forbidden("ACTOR_INACTIVE", "Tài khoản không hợp lệ hoặc đã bị khóa.");
        var state = await repo.GetPayOsStateAsync(paymentId, ct)
            ?? throw SchedulingException.NotFound("PAYMENT_NOT_FOUND", "Không tìm thấy giao dịch thanh toán.");
        if (actor.Role == UserRole.Member && state.MemberId != actorId)
            throw SchedulingException.Forbidden("PAYMENT_NOT_OWNED", "Không thể xem giao dịch của hội viên khác.");
        if (actor.Role is not (UserRole.Member or UserRole.Receptionist or UserRole.Manager))
            throw SchedulingException.Forbidden("ROLE_NOT_ALLOWED", "Vai trò không được xem giao dịch thanh toán.");
        await SyncWithPayOsAsync(actorId, paymentId, cancelStale: false, ct);
        return await GetPaymentAsync(actorId, paymentId, ct);
    }

    // Quản lý rà soát giao dịch trực tuyến đang chờ.
    // Nghiệp vụ: Kiểm tra quyền quản lý rồi đồng bộ, trả tổng số xử lý và lỗi; không bỏ qua xác minh thanh toán.
    public async Task<PayOsSyncSummary> SyncPendingPayOsAsync(int actorId, CancellationToken ct)
    {
        await MembershipRules.EnsureActiveRoleAsync(users, actorId, UserRole.Manager);
        return await SyncPendingAsync(actorId, ct);
    }

    // Tác vụ nền đồng bộ giao dịch trực tuyến đang chờ.
    // Nghiệp vụ: Dùng cùng quy tắc đồng bộ, không cần người thao tác trên trình duyệt; nhật ký gắn với hội viên.
    public Task<PayOsSyncSummary> SyncAllPendingPayOsAsync(CancellationToken ct) => SyncPendingAsync(null, ct);

    private async Task<PayOsSyncSummary> SyncPendingAsync(int? actorId, CancellationToken ct)
    {
        var ids = await repo.ListPendingPayOsPaymentIdsAsync(ct);
        int failed = 0, cancelled = 0, pending = 0, errors = 0;
        foreach (var id in ids)
        {
            try
            {
                switch (await SyncWithPayOsAsync(actorId, id, cancelStale: true, ct))
                {
                    case SyncOutcome.MarkedFailed: failed++; break;
                    case SyncOutcome.CancelledOnPayOs: cancelled++; break;
                    case SyncOutcome.StillPending: pending++; break;
                }
            }
            catch (SchedulingException) { errors++; }
        }
        return new(ids.Count, failed, cancelled, pending, errors);
    }

    // Không tự đánh dấu đã trả tiền; chỉ thông báo có chữ ký hợp lệ mới được cộng ví.
    private async Task<SyncOutcome> SyncWithPayOsAsync(int? actorId, int paymentId, bool cancelStale, CancellationToken ct)
    {
        var key = await repo.GetPayOsStateAsync(paymentId, ct);
        if (key is null || key.Status != PaymentStatus.Pending || key.Method != PaymentMethod.BankTransfer || !key.HasCheckoutLink)
            return SyncOutcome.Skipped;
        PaymentLink link;
        try { link = await payOs.PaymentRequests.GetAsync(key.OrderCode, new PayOS.Models.RequestOptions { CancellationToken = ct }); }
        catch (Exception) { throw new SchedulingException("PAYOS_UNAVAILABLE", "Không lấy được trạng thái từ PayOS. Vui lòng thử lại.", 502); }

        var outcome = SyncOutcome.MarkedFailed;
        if (link.Status == PaymentLinkStatus.Pending)
        {
            var stale = cancelStale && DateTimeOffset.TryParse(link.CreatedAt, out var createdAt) && createdAt.Add(PayOsLinkLifetime) < clock.UtcNow;
            if (!stale) return SyncOutcome.StillPending;
            try { await payOs.PaymentRequests.CancelAsync(key.OrderCode, "Quá hạn thanh toán", new PayOS.Models.RequestOptions<CancelPaymentLinkRequest> { CancellationToken = ct }); }
            catch (Exception) { throw new SchedulingException("PAYOS_UNAVAILABLE", "Không huỷ được link thanh toán trên PayOS.", 502); }
            outcome = SyncOutcome.CancelledOnPayOs;
        }
        else if (link.Status is not (PaymentLinkStatus.Cancelled or PaymentLinkStatus.Expired or PaymentLinkStatus.Failed))
            return SyncOutcome.StillPending; // Đã trả nhưng chờ thông báo, trả thiếu hoặc đang xử lý thì giữ nguyên để cổng thanh toán hoặc nhân viên xử lý.

        return await transaction.ExecuteAsync(async token =>
        {
            await transaction.LockAsync([new(ScheduleResourceType.Member, key.MemberId)], token);
            if (await repo.GetPaymentStatusAsync(key.OrderCode, token) != PaymentStatus.Pending) return SyncOutcome.Skipped;
            var payment = await repo.GetPaymentByOrderCodeAsync(key.OrderCode, token);
            if (payment is null) return SyncOutcome.Skipped;
            await MarkFailedAsync(payment, actorId ?? payment.MemberId, new { payment.PayOsOrderCode, PayOsStatus = link.Status.ToString(), Source = "payos-sync" }, token);
            return outcome;
        }, ct);
    }

    // Đăng ký địa chỉ công khai nhận xác nhận với cổng thanh toán.
    // Nghiệp vụ: Chỉ quản lý, địa chỉ công khai hợp lệ; không tạo giao dịch hoặc nạp tiền tại đây.
    public async Task<string> ConfirmWebhookUrlAsync(int actorId, string webhookUrl, CancellationToken ct)
    {
        await MembershipRules.EnsureActiveRoleAsync(users, actorId, UserRole.Manager);
        var result = await payOs.Webhooks.ConfirmAsync(webhookUrl);
        return result.WebhookUrl;
    }

    private async Task<CreatePaymentLinkResponse> CreatePayOsLinkAsync(Payment payment, CancellationToken ct)
    {
        var baseUrl = mailOptions.Value.FrontendUrl.TrimEnd('/');
        var description = $"Thanh toan {payment.Id}";
        var request = new CreatePaymentLinkRequest
        {
            OrderCode = payment.PayOsOrderCode,
            Amount = (long)payment.Amount,
            Description = description.Length > 25 ? description[..25] : description,
            ReturnUrl = $"{baseUrl}/payment/success?paymentId={payment.Id}",
            CancelUrl = $"{baseUrl}/payment/cancel?paymentId={payment.Id}",
            ExpiredAt = new DateTimeOffset(clock.UtcNow.Add(PayOsLinkLifetime)).ToUnixTimeSeconds(),
        };
        try { return await payOs.PaymentRequests.CreateAsync(request, new PayOS.Models.RequestOptions<CreatePaymentLinkRequest> { CancellationToken = ct }); }
        catch (Exception)
        {
            throw new SchedulingException("PAYOS_LINK_FAILED",
                "Không tạo được link thanh toán PayOS. Vui lòng thử lại.", 502);
        }
    }

    // Áp dụng một lần thanh toán thành công.
    // Nghiệp vụ: Ghi đã thanh toán; theo mục đích mà nạp ví hoặc kích hoạt gói, kèm nhật ký tương ứng.
    private async Task ConfirmPaymentAsync(Payment payment, int actorId, string reference, CancellationToken ct)
    {
        payment.Status = PaymentStatus.Paid;
        payment.PaidAt = clock.UtcNow;
        if (payment.Purpose == PaymentPurpose.WalletTopUp)
        {
            if (payment.SubscriptionId != null) throw SchedulingException.Conflict("PAYMENT_PURPOSE_MISMATCH", "Yêu cầu nạp không được kích hoạt gói.");
            await wallet.MoveAsync(payment.MemberId, actorId, payment.Amount, "OnlineTopUp", $"payment:{payment.Id}:credit", null, payment.Id, ct);
        }
        else if (payment.Subscription is not null) await ActivateOrMergeSubscriptionAsync(payment, ct);
        if (payment.Purpose == PaymentPurpose.LegacySubscription) await tiers.CheckUpgradeAsync(payment.MemberId, ct, payment.Amount);
        payment.InvoiceNumber = payment.Purpose == PaymentPurpose.WalletTopUp ? $"WAL-{payment.Id:D8}" : $"INV-{clock.LocalNow:yyyyMMdd}-{payment.SubscriptionId:D6}";
        await repo.AddAuditAsync(actorId, "Payment.Confirmed", nameof(Payment), payment.Id,
            new { payment.PayOsOrderCode, Reference = reference }, ct);
    }

    // Chỉ tính gia hạn khi xác nhận tiền đã đến; đơn mới vẫn chờ thanh toán trước thời điểm đó,
    // vì vậy gói cũ còn hạn chỉ được nối tiếp khi thu tiền thành công.
    private async Task ActivateOrMergeSubscriptionAsync(Payment payment, CancellationToken ct)
    {
        var sub = payment.Subscription!;
        var existing = await repo.GetLatestSubscriptionAsync(sub.MemberId, sub.PlanId, ct);
        if (existing is null) { sub.Status = SubscriptionStatus.Active; return; }

        if (sub.EndDate > existing.EndDate) existing.EndDate = sub.EndDate;
        existing.RemainingPtSessions += sub.RemainingPtSessions;
        sub.Status = SubscriptionStatus.Cancelled;
        payment.SubscriptionId = existing.Id;
        await repo.AddAuditAsync(payment.MemberId, "Subscription.MergedIntoExisting", nameof(MemberSubscription), existing.Id,
            new { AbsorbedSubscriptionId = sub.Id, existing.EndDate, existing.RemainingPtSessions }, ct);
    }

    private static DateOnly CalculateEndDate(DateOnly startDate, MembershipPlan plan) =>
        plan.DurationMonths > 0
            ? startDate.AddMonths(plan.DurationMonths).AddDays(-1)
            : startDate.AddDays(plan.DurationDays - 1);

    private async Task<(User Actor, User Member)> ResolveSubscribeTargetAsync(int actorId, int? requestedMemberId)
    {
        var actor = await users.GetByIdAsync(actorId)
            ?? throw SchedulingException.Forbidden("ACTOR_INACTIVE", "Tài khoản không hợp lệ hoặc đã bị khóa.");
        var targetId = actor.Role switch
        {
            UserRole.Member when requestedMemberId is null || requestedMemberId == actorId => actorId,
            UserRole.Member => throw SchedulingException.Forbidden("CANNOT_ACT_FOR_MEMBER",
                "Hội viên không thể đăng ký hộ người khác."),
            UserRole.Receptionist when requestedMemberId is not null => requestedMemberId.Value,
            UserRole.Receptionist => throw new SchedulingException("TARGET_MEMBER_REQUIRED", "Lễ tân phải chọn hội viên."),
            _ => throw SchedulingException.Forbidden("ROLE_NOT_ALLOWED", "Vai trò không được đăng ký gói."),
        };
        var member = targetId == actorId ? actor : await users.GetByIdAsync(targetId);
        if (member is null || member.Role != UserRole.Member)
            throw SchedulingException.NotFound("MEMBER_NOT_FOUND", "Không tìm thấy hội viên đang hoạt động.");
        return (actor, member);
    }

    private async Task<int> ResolveViewTargetAsync(int actorId, int? requestedMemberId)
    {
        var actor = await users.GetByIdAsync(actorId)
            ?? throw SchedulingException.Forbidden("ACTOR_INACTIVE", "Tài khoản không hợp lệ hoặc đã bị khóa.");
        int targetId;
        if (actor.Role == UserRole.Member)
        {
            if (requestedMemberId is not null && requestedMemberId != actorId)
                throw SchedulingException.Forbidden("SUBSCRIPTIONS_NOT_OWNED", "Không thể xem gói của hội viên khác.");
            targetId = actorId;
        }
        else if (actor.Role is UserRole.Receptionist or UserRole.Manager)
        {
            targetId = requestedMemberId
                ?? throw new SchedulingException("TARGET_MEMBER_REQUIRED", "Thiếu memberId.");
        }
        else
        {
            throw SchedulingException.Forbidden("ROLE_NOT_ALLOWED", "Vai trò không được xem gói hội viên.");
        }

        var target = targetId == actorId ? actor : await users.GetByIdAsync(targetId);
        if (target is null || target.Role != UserRole.Member)
            throw SchedulingException.NotFound("MEMBER_NOT_FOUND", "Không tìm thấy hội viên đang hoạt động.");
        return targetId;
    }

    private static SubscriptionDto ToSubscriptionDto(MemberSubscription subscription,
        IReadOnlyCollection<Sport> sports, DateOnly today, PaymentStatus? paymentStatus)
    {
        var status = subscription.Status == SubscriptionStatus.Active && subscription.EndDate < today
            ? SubscriptionStatus.Expired
            : subscription.Status;
        var remainingDays = status is SubscriptionStatus.Cancelled or SubscriptionStatus.Expired
            ? 0
            : Math.Max(0, subscription.EndDate.DayNumber - Math.Max(today.DayNumber, subscription.StartDate.DayNumber) + 1);
        var allowedSports = MembershipPlanPresentation.GetAllowedSports(subscription.Plan, sports)
            .Select(x => new PlanSportDto(x.Id, x.Name)).ToList();
        return new SubscriptionDto(subscription.Id, subscription.MemberId, subscription.PlanId, subscription.Plan.Name,
            subscription.StartDate, subscription.EndDate, status, subscription.RemainingPtSessions,
            subscription.IncludesGymAccess, MembershipPlanPresentation.GetPlanType(subscription.Plan),
            subscription.SnapshotPtCredits ?? subscription.Plan.PtSessionCredits, remainingDays, subscription.PurchaseOrderId == null && subscription.Plan.IncludesClasses,
            subscription.PurchaseOrderId != null ? subscription.SnapshotSportId : subscription.Plan.SportId,
            subscription.PurchaseOrderId != null ? sports.FirstOrDefault(x => x.Id == subscription.SnapshotSportId)?.Name : subscription.Plan.Sport?.Name,
            subscription.PurchaseOrderId != null ? sports.Where(x => subscription.SnapshotSportId == null || x.Id == subscription.SnapshotSportId).Select(x => new PlanSportDto(x.Id, x.Name)).ToList() : allowedSports,
            paymentStatus, subscription.PurchaseOrderId);
    }

    internal static PaymentDto ToPaymentDto(Payment payment, User member, string? planName) =>
        new(payment.Id, payment.MemberId, member.FullName, payment.SubscriptionId, planName, payment.Amount,
            payment.Method, payment.Status, payment.InvoiceNumber, payment.PaidAt, payment.CheckoutUrl,
            payment.QrCode, payment.ReceivedBy?.FullName, payment.Purpose);
}
