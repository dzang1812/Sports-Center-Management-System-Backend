using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Services.Membership;

namespace SportsCenter.API.Controllers;

[ApiController, Route("api/membership-plans")]
public sealed class MembershipPlansController(IMembershipPlanService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // Xem các gói đang mở bán.
    [HttpGet, AllowAnonymous] public Task<List<PlanDto>> List(CancellationToken ct) => service.ListAsync(activeOnly: true, ct);
    // Xem thông tin một gói trước khi đăng ký.
    [HttpGet("{id:int}"), AllowAnonymous]
    public Task<PlanDto> Get(int id, CancellationToken ct) => service.GetAsync(id, User.IsInRole("Manager"), ct);
    // Quản lý xem cả gói đang bán và đã ẩn.
    [HttpGet("all"), Authorize(Roles = "Manager")] public Task<List<PlanDto>> ListAll(CancellationToken ct) => service.ListAsync(activeOnly: false, ct);
    // Tạo gói tập riêng hoặc gói theo thời hạn.
    [HttpPost, Authorize(Roles = "Manager")] public Task<PlanDto> Create(UpsertPlanRequest request, CancellationToken ct) => service.CreateAsync(ActorId, request, ct);
    // Sửa cấu hình gói đang có.
    [HttpPut("{id:int}"), Authorize(Roles = "Manager")] public Task<PlanDto> Update(int id, UpsertPlanRequest request, CancellationToken ct) => service.UpdateAsync(ActorId, id, request, ct);
    // Ẩn gói để ngừng bán hoặc mở bán lại.
    [HttpPatch("{id:int}/status"), Authorize(Roles = "Manager")]
    public Task<PlanDto> SetStatus(int id, ChangePlanStatusRequest request, CancellationToken ct) => service.SetActiveAsync(ActorId, id, request.IsActive!.Value, ct);
}
