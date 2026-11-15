using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Services.Membership;

namespace SportsCenter.API.Controllers;

[ApiController, Authorize(Roles = "Member"), Route("api/me")]
public sealed class MyGymAccessController(IGymAccessService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // Lấy thẻ và quyền vào trung tâm của mình.
    [HttpGet("gym-card")]
    public Task<GymCardDto> Card(CancellationToken ct) => service.GetMyCardAsync(ActorId, ct);

    // Xem lịch sử vào và ra trung tâm.
    [HttpGet("gym-visits")]
    public Task<GymVisitHistoryResponse> Visits(
        DateOnly from, DateOnly to, int page = 1, int pageSize = 20, CancellationToken ct = default) =>
        service.ListVisitsAsync(ActorId, null, from, to, page, pageSize, ct);
}

[ApiController, Authorize(Roles = "Receptionist"), Route("api/gym-access")]
public sealed class GymAccessController(IGymAccessService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // Tra cứu thẻ hội viên sau khi quét mã.
    [HttpGet("cards/{cardCode}")]
    public Task<GymCardDto> Lookup(string cardCode, CancellationToken ct) =>
        service.LookupCardAsync(ActorId, cardCode, ct);

    // Tìm hội viên theo tên, thư điện tử, điện thoại hoặc mã thẻ.
    [HttpGet("members")]
    public Task<List<SportsCenter.Repository.Repositories.GymMemberSearchRow>> Search(string? q, CancellationToken ct) =>
        service.SearchMembersAsync(ActorId, q, ct);

    // Mở thẻ vào trung tâm của hội viên.
    [HttpGet("members/{memberId:int}/card")]
    public Task<GymCardDto> MemberCard(int memberId, CancellationToken ct) => service.GetMemberCardAsync(ActorId, memberId, ct);

    // Xác nhận hội viên vào tự tập.
    [HttpPost("check-ins")]
    public Task<GymScanResult> CheckIn(GymScanRequest request, CancellationToken ct) =>
        service.CheckInAsync(ActorId, request, ct);

    // Xác nhận hội viên ra khỏi trung tâm.
    [HttpPost("check-outs")]
    public Task<GymScanResult> CheckOut(GymScanRequest request, CancellationToken ct) =>
        service.CheckOutAsync(ActorId, request, ct);

    // Xem lịch sử vào/ra trung tâm để đối chiếu.
    [HttpGet("visits")]
    public Task<GymVisitHistoryResponse> Visits(
        DateOnly from, DateOnly to, int page = 1, int pageSize = 20, CancellationToken ct = default) =>
        service.ListVisitsAsync(ActorId, null, from, to, page, pageSize, ct);
}

[ApiController, Authorize(Roles = "Receptionist,Manager")]
public sealed class MemberGymVisitsController(IGymAccessService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // Xem lượt vào trung tâm của một hội viên.
    [HttpGet("api/members/{memberId:int}/gym-visits")]
    public Task<GymVisitHistoryResponse> Visits(
        int memberId, DateOnly from, DateOnly to, int page = 1, int pageSize = 20, CancellationToken ct = default) =>
        service.ListVisitsAsync(ActorId, memberId, from, to, page, pageSize, ct);
}
