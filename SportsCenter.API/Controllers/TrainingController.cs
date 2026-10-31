using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Services.Auth;
using SportsCenter.Services.Training;

namespace SportsCenter.API.Controllers;

[ApiController, Authorize]
public class TrainingController(ITrainingService training) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("api/exercises")]
    public async Task<IActionResult> Exercises([FromQuery] string? muscleGroup, [FromQuery] string? q, CancellationToken ct) =>
        Ok(await training.ExercisesAsync(muscleGroup, q, ct));

    [HttpGet("api/exercises/muscle-groups")]
    public async Task<IActionResult> MuscleGroups(CancellationToken ct) => Ok(await training.MuscleGroupsAsync(ct));

    [HttpPost("api/exercises/seed"), Authorize(Roles = "Manager")]
    public async Task<IActionResult> Seed(CancellationToken ct)
    {
        var file = Path.Combine(AppContext.BaseDirectory, "SeedData", "exercises.json");
        return Ok(new { added = await training.SeedExercisesAsync(file, ct) });
    }

    // Body: [{ "name": "Barbell Bench Press", "imageUrl": "https://res.cloudinary.com/...", "videoUrl": null }]
    [HttpPost("api/exercises/import-media"), Authorize(Roles = "Manager")]
    public async Task<IActionResult> ImportMedia(List<ExerciseMediaDto> media, CancellationToken ct) =>
        Ok(new { updated = await training.ImportMediaAsync(media, ct) });

    [HttpGet("api/member/training-plans"), Authorize(Roles = "Member")]
    public async Task<IActionResult> Plans(CancellationToken ct) => Ok(await training.PlansAsync(UserId, ct));

    [HttpPost("api/member/training-plans"), Authorize(Roles = "Member")]
    public Task<IActionResult> Create(SavePlanDto dto, CancellationToken ct) => Save(null, dto, ct);

    [HttpPut("api/member/training-plans/{id:int}"), Authorize(Roles = "Member")]
    public Task<IActionResult> Update(int id, SavePlanDto dto, CancellationToken ct) => Save(id, dto, ct);

    [HttpPost("api/member/training-plans/{id:int}/activate"), Authorize(Roles = "Member")]
    public Task<IActionResult> Activate(int id, CancellationToken ct) =>
        Run(async () => { await training.ActivateAsync(UserId, id, ct); return NoContent(); });

    [HttpDelete("api/member/training-plans/{id:int}"), Authorize(Roles = "Member")]
    public Task<IActionResult> Delete(int id, CancellationToken ct) =>
        Run(async () => { await training.DeleteAsync(UserId, id, ct); return NoContent(); });

    private Task<IActionResult> Save(int? id, SavePlanDto dto, CancellationToken ct) =>
        Run(async () => Ok(await training.SavePlanAsync(UserId, id, dto, ct)));

    private static async Task<IActionResult> Run(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (AuthException ex) { return new BadRequestObjectResult(new { message = ex.Message }); }
    }
}
