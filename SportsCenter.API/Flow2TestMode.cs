using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.API;

public sealed class Flow2TestMode(
    IHttpContextAccessor httpContextAccessor,
    IWebHostEnvironment environment,
    IConfiguration configuration) : IFlow2TestMode
{
    public const string HeaderName = "X-Flow2-Test-Mode";

    public bool IsAvailable =>
        environment.IsDevelopment() &&
        configuration.GetValue<bool>("EnableFlow2TimeTestMode");

    public bool IsRequested =>
        string.Equals(
            httpContextAccessor.HttpContext?.Request.Headers[HeaderName].ToString(),
            bool.TrueString,
            StringComparison.OrdinalIgnoreCase);

    public bool IsActive
    {
        get
        {
            var user = httpContextAccessor.HttpContext?.User;
            return IsAvailable &&
                IsRequested &&
                user?.Identity?.IsAuthenticated == true &&
                (user.IsInRole("Manager") || user.IsInRole("Admin"));
        }
    }
}

[ApiController]
[Route("api/dev/flow2-test-mode")]
[Authorize(Roles = "Manager,Admin")]
public sealed class Flow2TestModeController(IFlow2TestMode testMode) : ControllerBase
{
    private static readonly string[] SupportedTimeGuards =
    [
        "class session move: target must be in the future",
        "class booking: configured open/close window (session must still be in the future)",
        "class cancellation: configured cutoff only (started/finalized/attended sessions stay blocked)",
        "PT booking: configured open/close window (slot must still be in the future)",
        "PT reschedule: configured cutoff only (occurred/finalized bookings stay blocked)",
        "class check-in: opens-before-start and closes-at-end window",
        "class check-in code: expiry time",
        "PT attendance: correct-day and has-started checks"
    ];

    [HttpGet]
    public IActionResult Get() => Ok(new
    {
        available = testMode.IsAvailable,
        requested = testMode.IsRequested,
        active = testMode.IsActive,
        header = Flow2TestMode.HeaderName,
        supportedTimeGuards = SupportedTimeGuards,
    });
}
