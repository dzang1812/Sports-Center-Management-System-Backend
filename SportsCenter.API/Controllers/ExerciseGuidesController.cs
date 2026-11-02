using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SportsCenter.Services.Training;

namespace SportsCenter.API.Controllers;

[ApiController, Authorize, Route("api/exercise-guides"), EnableRateLimiting("exercise-guides")]
public class ExerciseGuidesController(IExerciseGuideService guides) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string q = "", [FromQuery] int offset = 0, CancellationToken ct = default)
    {
        try { return Ok(await guides.SearchAsync(q, offset, ct)); }
        catch (ExerciseGuideException ex) { return StatusCode(ex.StatusCode, new { message = ex.Message }); }
    }
}
