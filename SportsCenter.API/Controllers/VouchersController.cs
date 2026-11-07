using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Services.Payments;

namespace SportsCenter.API.Controllers;

[ApiController, Route("api")]
public sealed class VouchersController(VoucherService service) : ControllerBase
{
    private int Actor => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("vouchers"), Authorize(Roles = "Manager,Receptionist")]
    public Task<List<VoucherDto>> List(CancellationToken ct) => service.ListAsync(Actor, ct);
    [HttpPost("vouchers"), Authorize(Roles = "Manager")]
    public Task<VoucherDto> Create(VoucherRequest body, CancellationToken ct) => service.CreateAsync(Actor, body, ct);
    [HttpPut("vouchers/{id:int}"), Authorize(Roles = "Manager")]
    public Task<VoucherDto> Update(int id, VoucherRequest body, CancellationToken ct) => service.UpdateAsync(Actor, id, body, ct);

    [HttpPost("vouchers/{id:int}/grants"), Authorize(Roles = "Manager,Receptionist")]
    public Task<MemberVoucherDto> Grant(int id, GrantVoucherRequest body, CancellationToken ct) => service.GrantAsync(Actor, id, body.MemberId, ct);
    [HttpGet("members/{memberId:int}/vouchers"), Authorize(Roles = "Manager,Receptionist")]
    public Task<List<MemberVoucherDto>> ForMember(int memberId, CancellationToken ct) => service.ListForMemberAsync(memberId, ct);
    [HttpGet("me/vouchers"), Authorize(Roles = "Member")]
    public Task<List<MemberVoucherDto>> Mine(CancellationToken ct) => service.ListForMemberAsync(Actor, ct);
    [HttpPost("vouchers/preview"), Authorize(Roles = "Member,Receptionist,Manager")]
    public Task<VoucherPreview> Preview(VoucherPreviewRequest body, CancellationToken ct) => service.PreviewAsync(Actor, body, ct);
}
