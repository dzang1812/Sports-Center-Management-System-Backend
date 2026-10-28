using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Repository.Entities;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.API.Controllers;

[ApiController, Route("api/coach/availability"), Authorize(Roles = "Coach")]
public sealed class CoachAvailabilityController(IPtService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpGet] public Task<List<AvailabilityItem>> Get(CancellationToken ct) => service.GetAvailabilityAsync(ActorId, ct);
    [HttpPut] public Task<List<AvailabilityItem>> Put(ReplaceAvailabilityRequest request, CancellationToken ct) => service.ReplaceAvailabilityAsync(ActorId, request, ct);
}

[ApiController, Route("api/pt")]
public sealed class PtController(IPtService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpGet("coaches"), AllowAnonymous] public Task<List<PtCoachDto>> Coaches(int? sportId, CancellationToken ct) => service.ListCoachesAsync(sportId, ct);
    [HttpGet("coaches/{coachId:int}/slots"), AllowAnonymous]
    public Task<List<PtSlotDto>> Slots(int coachId, int sportId, DateOnly from, DateOnly to, CancellationToken ct) => service.ListSlotsAsync(coachId, sportId, from, to, ct);
    [HttpPost("bookings"), Authorize(Roles = "Member,Receptionist")]
    public Task<PtBookingDto> Book(CreatePtBookingRequest request, CancellationToken ct) => service.BookAsync(ActorId, request, ct);
    [HttpGet("bookings"), Authorize(Roles = "Manager")]
    public Task<PagedResponse<PtBookingListItem>> Bookings(DateOnly? from, DateOnly? to, int? memberId,
        int? coachId, int? sportId, BookingStatus? status, int page = 1, int pageSize = 20, CancellationToken ct = default) =>
        service.ListBookingsAsync(ActorId, from, to, memberId, coachId, sportId, status, page, pageSize, ct);
    [HttpPatch("bookings/{id:int}/manager-cancel"), Authorize(Roles = "Manager")]
    public async Task<IActionResult> ManagerCancel(int id, ManagerCancelPtBookingRequest request, CancellationToken ct)
    { await service.ManagerCancelAsync(ActorId, id, request, ct); return NoContent(); }
    [HttpDelete("bookings/{id:int}"), Authorize(Roles = "Member,Receptionist")]
    public async Task<IActionResult> Cancel(int id, [FromBody] CancelPtBookingRequest? request, CancellationToken ct)
    { await service.CancelAsync(ActorId, id, request ?? new(null), ct); return NoContent(); }
    [HttpPut("bookings/{id:int}/reschedule"), Authorize(Roles = "Member,Receptionist")]
    public Task<PtBookingDto> Reschedule(int id, ReschedulePtBookingRequest request, CancellationToken ct) => service.RescheduleAsync(ActorId, id, request, ct);
    [HttpPatch("bookings/{id:int}/coach-cancel"), Authorize(Roles = "Coach")]
    public async Task<IActionResult> CoachCancel(int id, CancelPtBookingRequest? request, CancellationToken ct)
    { await service.CoachCancelAsync(ActorId, id, request ?? new(null), ct); return NoContent(); }
}
