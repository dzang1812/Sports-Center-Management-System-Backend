using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Services.Auth;
using SportsCenter.Services.Training;

namespace SportsCenter.API.Controllers;

// Coach side of Flow 4: students, plans for a member or a whole class.
[ApiController, Authorize(Roles = "Coach"), Route("api/coach")]
public sealed class CoachTrainingController(ICoachTrainingService service) : ControllerBase
{
    private int CoachId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // Members the coach teaches (class bookings, course enrollments, PT bookings); q = name/email/phone.
    [HttpGet("students")]
    public Task<List<CoachStudentDto>> Students(string? q, CancellationToken ct) => service.StudentsAsync(CoachId, q, ct);

    [HttpGet("classes")]
    public Task<List<CoachClassDto>> Classes(CancellationToken ct) => service.ClassesAsync(CoachId, ct);

    [HttpGet("training-plans")]
    public Task<List<PlanDto>> Plans(int? memberId, int? classId, CancellationToken ct) => service.PlansAsync(CoachId, memberId, classId, ct);

    [HttpPost("training-plans")]
    public Task<IActionResult> CreatePlan(CoachSavePlanDto dto, CancellationToken ct) =>
        Run(async () => Ok(await service.SavePlanAsync(CoachId, null, dto, ct)));

    [HttpPut("training-plans/{id:int}")]
    public Task<IActionResult> UpdatePlan(int id, CoachSavePlanDto dto, CancellationToken ct) =>
        Run(async () => Ok(await service.SavePlanAsync(CoachId, id, dto, ct)));

    [HttpDelete("training-plans/{id:int}")]
    public async Task<IActionResult> DeletePlan(int id, CancellationToken ct)
    {
        await service.DeletePlanAsync(CoachId, id, ct);
        return NoContent();
    }

    [HttpGet("sessions/class/{sessionId:int}/results")]
    public Task<List<TrainingResultDto>> ClassResults(int sessionId, CancellationToken ct) =>
        service.ClassSessionResultsAsync(CoachId, sessionId, ct);

    [HttpPut("sessions/class/{sessionId:int}/results")]
    public Task<List<TrainingResultDto>> SaveClassResults(int sessionId, SaveTrainingResultsRequest request, CancellationToken ct) =>
        service.SaveClassSessionResultsAsync(CoachId, sessionId, request, ct);

    [HttpGet("sessions/pt/{bookingId:int}/results")]
    public Task<List<TrainingResultDto>> PtResults(int bookingId, CancellationToken ct) => service.PtResultsAsync(CoachId, bookingId, ct);

    [HttpPut("sessions/pt/{bookingId:int}/results")]
    public Task<List<TrainingResultDto>> SavePtResults(int bookingId, SaveTrainingResultsRequest request, CancellationToken ct) =>
        service.SavePtResultsAsync(CoachId, bookingId, request, ct);

    // A student's full result history (every coach), for progress review.
    [HttpGet("students/{memberId:int}/results")]
    public Task<List<TrainingResultDto>> StudentResults(int memberId, CancellationToken ct) => service.StudentResultsAsync(CoachId, memberId, ct);

    // Plan-content validation is shared with the member endpoints, which report it as AuthException.
    private static async Task<IActionResult> Run(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (AuthException ex) { return new BadRequestObjectResult(new { message = ex.Message }); }
    }
}

// Member side of Flow 4: what coaches recorded about me. Plans are listed by GET /api/member/training-plans.
[ApiController, Authorize(Roles = "Member"), Route("api/me")]
public sealed class MyTrainingController(ICoachTrainingService service) : ControllerBase
{
    private int MemberId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("training-results")]
    public Task<List<TrainingResultDto>> Results(CancellationToken ct) => service.MyResultsAsync(MemberId, ct);
}
