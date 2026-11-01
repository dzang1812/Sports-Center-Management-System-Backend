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

    // Plan-content validation is shared with the member endpoints, which report it as AuthException.
    private static async Task<IActionResult> Run(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (AuthException ex) { return new BadRequestObjectResult(new { message = ex.Message }); }
    }
}
