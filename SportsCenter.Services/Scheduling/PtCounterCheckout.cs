using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Membership;
using SportsCenter.Services.Payments;

namespace SportsCenter.Services.Scheduling;

public record PtCounterCheckoutRequest(int MemberId, int CoachId, int SportId, DateOnly Date,
    TimeOnly StartTime, int? RoomId, PaymentMethod Method, decimal ExpectedPrice);
public record PtCounterCheckoutDto(int PaymentId, PtBookingDto? Booking, PaymentDto? Payment);
internal record PtCounterDraft(int PaymentId, PtBookingDto? Booking);
internal record PtCounterIntent(int PaymentId, CreatePtBookingRequest Booking);

public sealed partial class PtService
{
    // Lễ tân thu tiền mặt hoặc tạo mã chuyển khoản cho buổi tập riêng.
    // Nghiệp vụ: Kiểm tra giờ tập và giá; tiền mặt ghi nạp rồi đặt trong cùng giao dịch dữ liệu, chuyển khoản lưu ý định đặt để chờ tiền thật.
    public async Task<PtCounterCheckoutDto> CounterCheckoutAsync(int actorId, PtCounterCheckoutRequest request,
        string key, CancellationToken ct)
    {
        await users.EnsureActiveActorAsync(actorId, UserRole.Receptionist, ct);
        if (request.Method is not (PaymentMethod.Cash or PaymentMethod.BankTransfer))
            throw new SchedulingException("PAYMENT_METHOD_INVALID", "Chọn tiền mặt hoặc chuyển khoản QR.");
        var memberId = await ResolveTargetMemberAsync(actorId, request.MemberId, ct);
        var booking = new CreatePtBookingRequest(request.CoachId, request.SportId, request.Date,
            request.StartTime, request.RoomId, memberId, PtFundingSource.Wallet, request.ExpectedPrice);
        var draft = await wallet.ExecuteAsync(actorId, "pt-counter-checkout", key, request, async token =>
        {
            await LockBookingResourcesAsync(memberId, request.CoachId, request.SportId, request.RoomId, token);
            await ValidateBookingTargetAsync(memberId, request.CoachId, request.SportId, request.Date,
                request.StartTime, request.RoomId, null, token);
            if (clock.ToUtc(request.Date, request.StartTime) <= clock.UtcNow)
                throw SchedulingException.Conflict("BOOKING_CLOSED", "Không thu tiền cho buổi PT đã bắt đầu.");
            var sport = await catalog.GetSportAsync(request.SportId, token);
            if (sport?.PtSinglePrice is not { } price)
                throw SchedulingException.Conflict("PT_PRICE_UNSET", "Trung tâm chưa cấu hình giá PT lẻ.");
            Flow2CommerceService.EnsureExpectedPrice(request.ExpectedPrice, price);
            WalletService.ValidateMoney(price);
            var cash = request.Method == PaymentMethod.Cash;
            var reference = "pt-counter-cash:" + actorId + ":" + key;
            var payment = new Payment
            {
                MemberId = memberId, ReceivedById = actorId, Amount = price, Method = request.Method,
                Purpose = PaymentPurpose.WalletTopUp, Status = cash ? PaymentStatus.Paid : PaymentStatus.Pending,
                ProviderReference = cash ? reference : null, PaidAt = cash ? clock.UtcNow : default,
                Note = "PT tại quầy; dùng sổ ví hiện có để ghi nhận thu tiền và chi phí booking."
            };
            commerceRepo.Add(payment);
            await transaction.SaveChangesAsync(token);
            payment.PayOsOrderCode = payment.Id;
            if (cash)
            {
                payment.InvoiceNumber = $"WAL-{payment.Id:D8}";
                await wallet.MoveAsync(memberId, actorId, price, "CashTopUp", $"payment:{payment.Id}:credit", null, payment.Id, token);
                await transaction.SaveChangesAsync(token);
                var result = await BookCoreAsync(actorId, memberId, booking, token);
                audit.AddAudit(actorId, "PtCounter.CashCompleted", nameof(Payment), payment.Id,
                    new { result.Id, memberId, price, reference });
                return new PtCounterDraft(payment.Id, result);
            }

            // Gắn một thanh toán với ý định đặt lịch bất biến do máy chủ lưu; không thêm cấu trúc dữ liệu mới.
            var json = JsonSerializer.Serialize(new PtCounterIntent(payment.Id, booking));
            commerceRepo.Add(new CommerceRequest { ActorId = actorId, Operation = "pt-counter-payment",
                Key = payment.Id.ToString(), PayloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))), ResultJson = json });
            audit.AddAudit(actorId, "PtCounter.TransferCreated", nameof(Payment), payment.Id,
                new { memberId, price, request.CoachId, request.SportId, request.Date, request.StartTime });
            return new PtCounterDraft(payment.Id, null);
        }, ct);
        var paymentDto = draft.Booking is null
            ? await payments.GetCounterTopUpCheckoutAsync(actorId, draft.PaymentId, ct) : null;
        return new(draft.PaymentId, draft.Booking, paymentDto);
    }

    // Hoàn tất đặt buổi tập riêng sau xác nhận chuyển khoản.
    // Nghiệp vụ: Giao dịch đã trả tiền, đúng lễ tân, hội viên và ý định đặt; khóa chống trùng ổn định, không tự xác nhận hoặc cộng tiền lại.
    public async Task<PtBookingDto> CompleteCounterCheckoutAsync(int actorId, int paymentId, CancellationToken ct)
    {
        await users.EnsureActiveActorAsync(actorId, UserRole.Receptionist, ct);
        var saved = await commerceRepo.FindRequestAsync(actorId, "pt-counter-payment", paymentId.ToString(), ct)
            ?? throw SchedulingException.NotFound("PT_CHECKOUT_NOT_FOUND", "Không tìm thấy yêu cầu PT của thu ngân này.");
        var intent = JsonSerializer.Deserialize<PtCounterIntent>(saved.ResultJson)
            ?? throw SchedulingException.Conflict("PT_CHECKOUT_INVALID", "Dữ liệu yêu cầu thanh toán không hợp lệ.");
        var payment = await membershipRepo.GetPaymentWithDetailsAsync(paymentId, ct)
            ?? throw SchedulingException.NotFound("PAYMENT_NOT_FOUND", "Không tìm thấy giao dịch.");
        if (intent.PaymentId != paymentId || payment.ReceivedById != actorId || payment.MemberId != intent.Booking.MemberId ||
            payment.Purpose != PaymentPurpose.WalletTopUp || payment.Method != PaymentMethod.BankTransfer ||
            payment.SubscriptionId != null || payment.Amount != intent.Booking.ExpectedPrice)
            throw SchedulingException.Conflict("PAYMENT_PURPOSE_MISMATCH", "Thanh toán không khớp yêu cầu đặt PT.");
        if (payment.Status != PaymentStatus.Paid)
            throw SchedulingException.Conflict("PAYMENT_NOT_PAID", payment.Status == PaymentStatus.Pending
                ? "Chưa nhận được xác nhận chuyển khoản từ PayOS. Khách thanh toán xong, chờ vài giây rồi bấm Hoàn thành."
                : "Thanh toán đã thất bại hoặc bị hủy; chưa tạo booking PT.");
        // Chỉ thông báo từ cổng thanh toán được cộng ví; nút hoàn tất không tự xác nhận chuyển khoản hay cộng tiền hai lần.
        // Khóa chống trùng theo thanh toán ngăn yêu cầu hoàn tất đồng thời hoặc gửi lặp thu tiền hai lần.
        return await BookAsync(actorId, intent.Booking, ct, $"pt-counter-payment:{paymentId}");
    }
}
