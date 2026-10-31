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

    // Member: own request. Receptionist/Manager: on behalf of request.MemberId.
    [HttpPost, Authorize(Roles = "Member,Receptionist,Manager")]
    public Task<SupportRequestDto> Create(CreateSupportRequest request, CancellationToken ct) => service.CreateAsync(ActorId, request, ct);

    [HttpGet("mine"), Authorize(Roles = "Member")]
    public Task<PagedResponse<SupportRequestDto>> Mine(int page = 1, int pageSize = 10, CancellationToken ct = default) =>
        service.ListMineAsync(ActorId, page, pageSize, ct);

    [HttpGet, Authorize(Roles = "Receptionist,Manager")]
    public Task<PagedResponse<SupportRequestDto>> List(SupportStatus? status, SupportCategory? category, string? q, int page = 1, int pageSize = 10,
        CancellationToken ct = default) => service.ListAsync(ActorId, status, category, q, page, pageSize, ct);

    [HttpGet("summary"), Authorize(Roles = "Receptionist,Manager")]
    public Task<SupportSummaryDto> Summary(CancellationToken ct) => service.SummaryAsync(ActorId, ct);

    [HttpGet("{id:int}"), Authorize(Roles = "Member,Receptionist,Manager")]
    public Task<SupportDetailDto> Get(int id, CancellationToken ct) => service.GetAsync(ActorId, id, ct);

    [HttpPost("{id:int}/messages"), Authorize(Roles = "Member,Receptionist,Manager")]
    public Task<SupportDetailDto> Reply(int id, ReplySupportRequest request, CancellationToken ct) => service.ReplyAsync(ActorId, id, request, ct);

    // Staff: any status. Member: close (Closed) or reopen (Open) their own.
    [HttpPatch("{id:int}/status"), Authorize(Roles = "Member,Receptionist,Manager")]
    public Task<SupportRequestDto> UpdateStatus(int id, UpdateSupportStatusRequest request, CancellationToken ct) =>
        service.UpdateStatusAsync(ActorId, id, request.Status, ct);
}

[ApiController, Authorize(Roles = "Member,Receptionist,Manager"), Route("api/payments/history")]
public sealed class PaymentHistoryController(PaymentHistoryService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public Task<PagedResponse<PaymentDto>> List(int? memberId, int page = 1, int pageSize = 10, CancellationToken ct = default) =>
        service.ListAsync(ActorId, memberId, page, pageSize, ct);
}

[ApiController, Authorize(Roles = "Manager"), Route("api/reports")]
public sealed class ReportTrendsController(ReportTrendService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("trend")]
    public Task<List<ReportTrendPoint>> Trend(DateOnly from, DateOnly to, string groupBy = "day", CancellationToken ct = default) =>
        service.TrendAsync(ActorId, from, to, groupBy, ct);

    [HttpGet("class-fill")]
    public Task<List<ClassFillDto>> ClassFill(CancellationToken ct) => service.ClassFillAsync(ActorId, ct);
}

[ApiController, Authorize(Roles = "Coach"), Route("api/coach/messages")]
public sealed class CoachMessagesController(CoachMessageService service) : ControllerBase
{
    private int CoachId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost]
    public Task<CoachMessageResult> Send(CoachMessageRequest request, CancellationToken ct) => service.SendAsync(CoachId, request, ct);
}
