using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Repository.Entities;
using SportsCenter.Services.Auth;
using SportsCenter.Services.Membership;
using SportsCenter.Services.Operations;
using SportsCenter.Services.Scheduling;
using SportsCenter.Services.Users;

namespace SportsCenter.API.Controllers;

[ApiController, Authorize(Roles = "Receptionist,Manager"), Route("api/members")]
public sealed class MemberSignupController(IUserAdminService admin) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // Tạo tài khoản hội viên tại quầy.
    [HttpPost]
    public async Task<IActionResult> Create(CreateMemberRequest request)
    {
        try { return Ok(await admin.CreateMemberAsync(ActorId, request)); }
        catch (AuthException ex) { return BadRequest(new { error = ex.Message }); }
    }
}

[ApiController, Authorize, Route("api/support-requests")]
public sealed class SupportRequestsController(SupportService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // Gửi yêu cầu hỗ trợ hoặc ghi nhận hộ hội viên.
    [HttpPost, Authorize(Roles = "Member,Receptionist,Manager")]
    public Task<SupportRequestDto> Create(CreateSupportRequest request, CancellationToken ct) => service.CreateAsync(ActorId, request, ct);

    // Xem yêu cầu hỗ trợ của mình.
    [HttpGet("mine"), Authorize(Roles = "Member")]
    public Task<PagedResponse<SupportRequestDto>> Mine(int page = 1, int pageSize = 10, CancellationToken ct = default) =>
        service.ListMineAsync(ActorId, page, pageSize, ct);

    // Nhân viên lọc hàng đợi hỗ trợ.
    [HttpGet, Authorize(Roles = "Receptionist,Manager")]
    public Task<PagedResponse<SupportRequestDto>> List(SupportStatus? status, SupportCategory? category, string? q, int page = 1, int pageSize = 10,
        CancellationToken ct = default) => service.ListAsync(ActorId, status, category, q, page, pageSize, ct);

    // Đếm các yêu cầu đang cần xử lý.
    [HttpGet("summary"), Authorize(Roles = "Receptionist,Manager")]
    public Task<SupportSummaryDto> Summary(CancellationToken ct) => service.SummaryAsync(ActorId, ct);

    // Đọc yêu cầu và các tin nhắn được phép xem.
    [HttpGet("{id:int}"), Authorize(Roles = "Member,Receptionist,Manager")]
    public Task<SupportDetailDto> Get(int id, CancellationToken ct) => service.GetAsync(ActorId, id, ct);

    // Phản hồi yêu cầu hoặc thêm ghi chú nội bộ.
    [HttpPost("{id:int}/messages"), Authorize(Roles = "Member,Receptionist,Manager")]
    public Task<SupportDetailDto> Reply(int id, ReplySupportRequest request, CancellationToken ct) => service.ReplyAsync(ActorId, id, request, ct);

    // Đóng, mở lại hoặc cập nhật trạng thái yêu cầu.
    [HttpPatch("{id:int}/status"), Authorize(Roles = "Member,Receptionist,Manager")]
    public Task<SupportRequestDto> UpdateStatus(int id, UpdateSupportStatusRequest request, CancellationToken ct) =>
        service.UpdateStatusAsync(ActorId, id, request.Status, ct);
}

[ApiController, Authorize(Roles = "Member,Receptionist,Manager"), Route("api/payments/history")]
public sealed class PaymentHistoryController(PaymentHistoryService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // Xem lịch sử thanh toán của hội viên.
    [HttpGet]
    public Task<PagedResponse<PaymentDto>> List(int? memberId, int page = 1, int pageSize = 10, CancellationToken ct = default) =>
        service.ListAsync(ActorId, memberId, page, pageSize, ct);
}

[ApiController, Authorize(Roles = "Receptionist,Manager"), Route("api/subscriptions/expiring")]
public sealed class ExpiringSubscriptionsController(ExpiringSubscriptionService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // Nhân viên theo dõi gói sắp hết hạn để tư vấn.
    [HttpGet]
    public Task<List<SportsCenter.Repository.Repositories.ExpiringSubscriptionRow>> List(int days = 7, CancellationToken ct = default) =>
        service.ListAsync(ActorId, days, ct);
}

[ApiController, Authorize(Roles = "Manager"), Route("api/reports")]
public sealed class ReportTrendsController(ReportTrendService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // Xem xu hướng doanh thu và đăng ký theo thời gian.
    [HttpGet("trend")]
    public Task<List<ReportTrendPoint>> Trend(DateOnly from, DateOnly to, string groupBy = "day", CancellationToken ct = default) =>
        service.TrendAsync(ActorId, from, to, groupBy, ct);

    // Xem tình hình lấp đầy lớp và số đăng ký.
    [HttpGet("class-fill")]
    public Task<List<ClassFillDto>> ClassFill(CancellationToken ct) => service.ClassFillAsync(ActorId, ct);
}

[ApiController, Authorize(Roles = "Coach"), Route("api/coach/messages")]
public sealed class CoachMessagesController(CoachMessageService service) : ControllerBase
{
    private int CoachId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // Huấn luyện viên gửi lời nhắn tới học viên.
    [HttpPost]
    public Task<CoachMessageResult> Send(CoachMessageRequest request, CancellationToken ct) => service.SendAsync(CoachId, request, ct);
}
