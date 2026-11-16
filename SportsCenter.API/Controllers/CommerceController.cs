using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Services.Payments;
using SportsCenter.Services.Membership;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.API.Controllers;

[ApiController, Route("api/wallet"), Authorize(Roles = "Member,Receptionist,Manager")]
public sealed class WalletController(WalletService wallet, ISubscriptionService payments) : ControllerBase
{
    private int Actor => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string Key => Request.Headers["Idempotency-Key"].FirstOrDefault() ?? "";
    // Xem số dư ví của hội viên.
    [HttpGet] public Task<WalletDto> Get(int? memberId, CancellationToken ct) => wallet.GetAsync(Actor, memberId, ct);
    // Xem lịch sử nạp, chi và hoàn tiền.
    [HttpGet("ledger")] public Task<PagedResponse<WalletEntryDto>> Ledger(int? memberId, int page = 1, CancellationToken ct = default) => wallet.LedgerAsync(Actor, memberId, page, ct);
    // Ghi nhận tiền nạp đã thu tại quầy.
    [HttpPost("cash-top-ups"), Authorize(Roles = "Receptionist")]
    public async Task<object> Cash(CashTopUpRequest body, CancellationToken ct) => new { paymentId = await wallet.CashAsync(Actor, body, Key, ct) };
    // Tạo mã thanh toán để nạp ví trực tuyến.
    [HttpPost("online-top-ups"), Authorize(Roles = "Member")]
    public Task<PaymentDto> Online(OnlineTopUpRequest body, CancellationToken ct) => payments.CreateWalletTopUpAsync(Actor, body, Key, ct);
}

[ApiController, Route("api/courses"), Authorize(Roles = "Member,Receptionist,Manager,Coach")]
public sealed class CoursesController(Flow2CommerceService service) : ControllerBase
{
    private int Actor => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string Key => Request.Headers["Idempotency-Key"].FirstOrDefault() ?? "";
    // Xem danh sách khóa nhiều tuần.
    [HttpGet] public Task<object> List(CancellationToken ct) => service.CoursesAsync(Actor, ct);
    // Xem trước lịch và học phí nguyên khóa.
    [HttpPost("preview"), Authorize(Roles = "Manager")]
    public Task<CoursePreview> Preview(CreateCourseRequest body, CancellationToken ct) => service.PreviewCourseAsync(Actor, body, ct);
    // Tạo khóa và toàn bộ buổi học trong một lần.
    [HttpPost, Authorize(Roles = "Manager")]
    public Task<CreatedCourse> Create(CreateCourseRequest body, CancellationToken ct) => service.CreateCourseAsync(Actor, body, Key, ct);
    // Xem lịch toàn khóa và chính sách trước khi mua.
    [HttpGet("{id:int}")] public Task<object> Detail(int id, CancellationToken ct) => service.CourseAsync(Actor, id, ct);
    // Sửa học phí và số buổi trước khi khóa có người mua.
    [HttpPut("{id:int}/terms"), Authorize(Roles = "Manager")]
    public Task<int> Terms(int id, CourseTermsRequest body, CancellationToken ct) => service.TermsAsync(Actor, id, body, Key, ct);
    // Mở hoặc đóng nhận đăng ký khóa.
    [HttpPut("{id:int}/publish"), Authorize(Roles = "Manager")]
    public Task<int> Publish(int id, CoursePublishRequest body, CancellationToken ct) => service.PublishAsync(Actor, id, body, Key, ct);
    // Điều chỉnh hạn chót đăng ký còn hợp lệ.
    [HttpPut("{id:int}/enrollment-cutoff"), Authorize(Roles = "Manager")]
    public Task<int> Cutoff(int id, CourseCutoffRequest body, CancellationToken ct) => service.CutoffAsync(Actor, id, body, Key, ct);
    // Mua nguyên khóa hoặc đăng ký hộ hội viên.
    [HttpPost("{id:int}/enrollments"), Authorize(Roles = "Member,Receptionist")]
    public Task<EnrollmentResult> Buy(int id, BuyRequest body, CancellationToken ct) => service.EnrollAsync(Actor, id, body, Key, ct);
    // Đối chiếu ai đã đăng ký khóa và số tiền thực trả.
    [HttpGet("enrollments"), Authorize(Roles = "Member,Receptionist,Manager")]
    public Task<object> Enrollments(int? memberId, int? courseId, CancellationToken ct) => service.EnrollmentsAsync(Actor, memberId, courseId, ct);
    // Hủy đăng ký nguyên khóa trước khai giảng.
    [HttpPost("enrollments/{id:int}/cancel"), Authorize(Roles = "Member,Receptionist")]
    public Task<int> CancelEnrollment(int id, CourseCancelRequest body, CancellationToken ct) => service.CancelEnrollmentAsync(Actor, id, body.Reason, Key, ct);
    // Gửi yêu cầu hỗ trợ gắn với đăng ký nguyên khóa.
    [HttpPost("enrollments/{id:int}/support"), Authorize(Roles = "Member,Receptionist")]
    public Task<int> Support(int id, CourseCancelRequest body, CancellationToken ct) => service.RequestSupportAsync(Actor, id, body.Reason, Key, ct);
    // Xem yêu cầu hỗ trợ của khóa học.
    [HttpGet("{id:int}/support-requests"), Authorize(Roles = "Manager")]
    public Task<object> SupportRequests(int id, CancellationToken ct) => service.SupportRequestsAsync(Actor, id, ct);
    // Trung tâm hủy toàn khóa và xử lý quyền lợi còn lại.
    [HttpPost("{id:int}/cancel"), Authorize(Roles = "Manager")]
    public Task<int> Cancel(int id, CourseCancelRequest body, CancellationToken ct) => service.CancelCourseAsync(Actor, id, body, Key, ct);
    // Hủy một buổi khóa: chọn học bù hoặc hoàn phân bổ.
    [HttpPost("{id:int}/sessions/{sessionId:int}/cancel"), Authorize(Roles = "Manager")]
    public Task<int> CancelSession(int id, int sessionId, CourseSessionCancellation body, CancellationToken ct) => service.CancelSessionAsync(Actor, id, sessionId, body, Key, ct);
}

[ApiController, Route("api/membership-plans"), Authorize(Roles = "Member,Receptionist")]
public sealed class MembershipPlanPurchasesController(MembershipWalletPurchaseService service) : ControllerBase
{
    private int Actor => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    // Mua hoặc gia hạn gói tự tập theo thời hạn bằng ví.
    [HttpPost("{planId:int}/purchase")]
    public Task<MembershipPurchaseResult> Buy(int planId, BuyRequest body, CancellationToken ct) =>
        service.BuyAsync(Actor, planId, body, Request.Headers["Idempotency-Key"].FirstOrDefault() ?? "", ct);
}

[ApiController, Route("api/pt/packages"), Authorize(Roles = "Member,Receptionist")]
public sealed class PtPackagesController(Flow2CommerceService service) : ControllerBase
{
    private int Actor => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string Key => Request.Headers["Idempotency-Key"].FirstOrDefault() ?? "";
    // Mua gói tập riêng bằng ví để nhận lượt tập.
    [HttpPost("{planId:int}/purchase")]
    public Task<PackageResult> Buy(int planId, BuyRequest body, CancellationToken ct) => service.BuyPackageAsync(Actor, planId, body, Key, ct);
    // Hoàn gói tập riêng chưa từng sử dụng hoặc giữ lượt.
    [HttpPost("subscriptions/{id:int}/refund")]
    public Task<int> Refund(int id, CancellationToken ct) => service.RefundPackageAsync(Actor, id, Key, ct);
}

[ApiController, Route("api/pt/prices"), Authorize(Roles = "Member,Receptionist,Manager")]
public sealed class PtPricesController(Flow2CommerceService service) : ControllerBase
{
    private int Actor => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    // Xem giá tập riêng buổi lẻ theo bộ môn.
    [HttpGet] public Task<object> List(CancellationToken ct) => service.PtPricesAsync(ct);
    // Cấu hình giá một buổi tập riêng.
    [HttpPut("{sportId:int}"), Authorize(Roles = "Manager")]
    public Task<int> Set(int sportId, PtPriceRequest request, CancellationToken ct) =>
        service.SetPtPriceAsync(Actor, sportId, request, Request.Headers["Idempotency-Key"].FirstOrDefault() ?? "", ct);
}
