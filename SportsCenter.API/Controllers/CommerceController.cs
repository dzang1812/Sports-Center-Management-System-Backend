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
    [HttpGet] public Task<WalletDto> Get(int? memberId, CancellationToken ct) => wallet.GetAsync(Actor, memberId, ct);
    [HttpGet("ledger")] public Task<PagedResponse<WalletEntryDto>> Ledger(int? memberId, int page = 1, CancellationToken ct = default) => wallet.LedgerAsync(Actor, memberId, page, ct);
    [HttpPost("cash-top-ups"), Authorize(Roles = "Receptionist")]
    public async Task<object> Cash(CashTopUpRequest body, CancellationToken ct) => new { paymentId = await wallet.CashAsync(Actor, body, Key, ct) };
    [HttpPost("online-top-ups"), Authorize(Roles = "Member")]
    public Task<PaymentDto> Online(OnlineTopUpRequest body, CancellationToken ct) => payments.CreateWalletTopUpAsync(Actor, body, Key, ct);
}

[ApiController, Route("api/courses"), Authorize(Roles = "Member,Receptionist,Manager,Coach")]
public sealed class CoursesController(Flow2CommerceService service) : ControllerBase
{
    private int Actor => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string Key => Request.Headers["Idempotency-Key"].FirstOrDefault() ?? "";
    [HttpGet] public Task<object> List(CancellationToken ct) => service.CoursesAsync(Actor, ct);
    [HttpPost("preview"), Authorize(Roles = "Manager")]
    public Task<CoursePreview> Preview(CreateCourseRequest body, CancellationToken ct) => service.PreviewCourseAsync(Actor, body, ct);
    [HttpPost, Authorize(Roles = "Manager")]
    public Task<CreatedCourse> Create(CreateCourseRequest body, CancellationToken ct) => service.CreateCourseAsync(Actor, body, Key, ct);
    [HttpGet("{id:int}")] public Task<object> Detail(int id, CancellationToken ct) => service.CourseAsync(Actor, id, ct);
    [HttpPut("{id:int}/terms"), Authorize(Roles = "Manager")]
    public Task<int> Terms(int id, CourseTermsRequest body, CancellationToken ct) => service.TermsAsync(Actor, id, body, Key, ct);
    [HttpPut("{id:int}/publish"), Authorize(Roles = "Manager")]
    public Task<int> Publish(int id, CoursePublishRequest body, CancellationToken ct) => service.PublishAsync(Actor, id, body, Key, ct);
    [HttpPut("{id:int}/enrollment-cutoff"), Authorize(Roles = "Manager")]
    public Task<int> Cutoff(int id, CourseCutoffRequest body, CancellationToken ct) => service.CutoffAsync(Actor, id, body, Key, ct);
    [HttpPost("{id:int}/enrollments"), Authorize(Roles = "Member,Receptionist")]
    public Task<EnrollmentResult> Buy(int id, BuyRequest body, CancellationToken ct) => service.EnrollAsync(Actor, id, body, Key, ct);
    [HttpGet("enrollments"), Authorize(Roles = "Member,Receptionist,Manager")]
    public Task<object> Enrollments(int? memberId, int? courseId, CancellationToken ct) => service.EnrollmentsAsync(Actor, memberId, courseId, ct);
    [HttpPost("enrollments/{id:int}/cancel"), Authorize(Roles = "Member,Receptionist")]
    public Task<int> CancelEnrollment(int id, CourseCancelRequest body, CancellationToken ct) => service.CancelEnrollmentAsync(Actor, id, body.Reason, Key, ct);
    [HttpPost("enrollments/{id:int}/support"), Authorize(Roles = "Member,Receptionist")]
    public Task<int> Support(int id, CourseCancelRequest body, CancellationToken ct) => service.RequestSupportAsync(Actor, id, body.Reason, Key, ct);
    [HttpGet("{id:int}/support-requests"), Authorize(Roles = "Manager")]
    public Task<object> SupportRequests(int id, CancellationToken ct) => service.SupportRequestsAsync(Actor, id, ct);
    [HttpPost("{id:int}/cancel"), Authorize(Roles = "Manager")]
    public Task<int> Cancel(int id, CourseCancelRequest body, CancellationToken ct) => service.CancelCourseAsync(Actor, id, body, Key, ct);
    [HttpPost("{id:int}/sessions/{sessionId:int}/cancel"), Authorize(Roles = "Manager")]
    public Task<int> CancelSession(int id, int sessionId, CourseSessionCancellation body, CancellationToken ct) => service.CancelSessionAsync(Actor, id, sessionId, body, Key, ct);
}

[ApiController, Route("api/pt/packages"), Authorize(Roles = "Member,Receptionist")]
public sealed class PtPackagesController(Flow2CommerceService service) : ControllerBase
{
    private int Actor => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string Key => Request.Headers["Idempotency-Key"].FirstOrDefault() ?? "";
    [HttpPost("{planId:int}/purchase")]
    public Task<PackageResult> Buy(int planId, BuyRequest body, CancellationToken ct) => service.BuyPackageAsync(Actor, planId, body, Key, ct);
    [HttpPost("subscriptions/{id:int}/refund")]
    public Task<int> Refund(int id, CancellationToken ct) => service.RefundPackageAsync(Actor, id, Key, ct);
}

[ApiController, Route("api/pt/prices"), Authorize(Roles = "Member,Receptionist,Manager")]
public sealed class PtPricesController(Flow2CommerceService service) : ControllerBase
{
    private int Actor => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpGet] public Task<object> List(CancellationToken ct) => service.PtPricesAsync(ct);
    [HttpPut("{sportId:int}"), Authorize(Roles = "Manager")]
    public Task<int> Set(int sportId, PtPriceRequest request, CancellationToken ct) =>
        service.SetPtPriceAsync(Actor, sportId, request, Request.Headers["Idempotency-Key"].FirstOrDefault() ?? "", ct);
}
