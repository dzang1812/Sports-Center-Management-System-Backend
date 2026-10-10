using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.API.Controllers;

[ApiController, Authorize(Roles = "Member"), Route("api/me")]
public sealed class MyScheduleController(IScheduleAttendanceService service) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpGet("schedule")] public Task<List<PersonalScheduleItem>> Schedule(DateOnly from, DateOnly to, bool includeCancelled = false, CancellationToken ct = default) => service.GetPersonalScheduleAsync(UserId, from, to, includeCancelled, ct);
    [HttpGet("attendance")] public Task<AttendanceHistoryResponse> Attendance(int year, int month, CancellationToken ct) => service.GetAttendanceHistoryAsync(UserId, year, month, ct);
}

[ApiController, Authorize(Roles = "Coach"), Route("api/coach/sessions")]
public sealed class CoachSessionsController(IScheduleAttendanceService service) : ControllerBase
{
    private int CoachId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpGet] public Task<List<CoachScheduleItem>> Schedule(DateOnly from, DateOnly to, CancellationToken ct) => service.GetCoachScheduleAsync(CoachId, from, to, ct);
    [HttpGet("class/{sessionId:int}/roster")] public Task<List<RosterItem>> ClassRoster(int sessionId, CancellationToken ct) => service.GetClassRosterAsync(CoachId, sessionId, ct);
    [HttpGet("pt/{bookingId:int}/roster")] public Task<List<RosterItem>> PtRoster(int bookingId, CancellationToken ct) => service.GetPtRosterAsync(CoachId, bookingId, ct);
}

[ApiController, Authorize(Roles = "Coach")]
public sealed class AttendanceController(IScheduleAttendanceService service) : ControllerBase
{
    private int CoachId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpPut("api/sessions/{sessionId:int}/attendance")]
    public Task<List<RosterItem>> ClassAttendance(int sessionId, BulkAttendanceRequest request, CancellationToken ct) => service.MarkClassAttendanceAsync(CoachId, sessionId, request, ct);
    [HttpPut("api/pt/bookings/{bookingId:int}/attendance")]
    public Task<List<RosterItem>> PtAttendance(int bookingId, BulkAttendanceRequest request, CancellationToken ct) => service.MarkPtAttendanceAsync(CoachId, bookingId, request, ct);
}
// done 