using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.API.Controllers;

[ApiController, Authorize(Roles = "Member,Receptionist")]
public sealed class ClassBookingsController(IClassBookingService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost("api/sessions/{sessionId:int}/bookings")]
    public Task<BookingDto> Book(int sessionId, BookForMemberRequest request, CancellationToken ct) => service.BookAsync(ActorId, sessionId, request, ct);

    [HttpDelete("api/bookings/{bookingId:int}")]
    public async Task<IActionResult> Cancel(int bookingId, [FromQuery] string? reason, CancellationToken ct)
    { await service.CancelAsync(ActorId, bookingId, reason, ct); return NoContent(); }
}
