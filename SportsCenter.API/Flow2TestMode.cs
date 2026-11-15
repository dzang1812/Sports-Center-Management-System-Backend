using System.Threading;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.API;

public sealed class Flow2TimeRulesState : IFlow2TimeRules
{
    private int enforceTimeRules;

    public Flow2TimeRulesState(IConfiguration configuration)
    {
        IsAvailable = configuration.GetValue<bool>("Flow2TimeRules:AllowGlobalSwitch");
        var configuredDefault = configuration.GetValue<bool?>("Flow2TimeRules:EnforceTimeRules") ?? true;
        enforceTimeRules = IsAvailable && !configuredDefault ? 0 : 1;
    }

    public bool IsAvailable { get; }
    public bool EnforceTimeRules => Volatile.Read(ref enforceTimeRules) == 1;

    public bool TrySetEnforcement(bool enforce)
    {
        if (!IsAvailable) return false;
        Interlocked.Exchange(ref enforceTimeRules, enforce ? 1 : 0);
        return true;
    }
}

public sealed record UpdateFlow2TimeRulesRequest(bool? EnforceTimeRules);

[ApiController]
[Route("api/dev/flow2-time-rules")]
[Authorize]
public sealed class Flow2TimeRulesController(IFlow2TimeRules timeRules, IScheduleUserRepository users,
    ILogger<Flow2TimeRulesController> logger) : ControllerBase
{
    private static readonly string[] BypassedTimeGuards =
    [
        "class and PT booking open/close windows",
        "legacy class and PT subscription start/end dates",
        "class booking cancellation cutoff",
        "legacy PT cancellation and reschedule cutoffs",
        "class session generation and move future-date restrictions",
        "class check-in open/close window",
        "class check-in code expiry",
        "PT attendance correct-day and has-started restrictions"
    ];

    private static readonly string[] PreservedIntegrityGuards =
    [
        "authentication and role authorization",
        "cancelled/completed/finalized/attended state protections",
        "capacity, duplicate booking and schedule conflicts",
        "payment and subscription entitlement",
        "course enrollment cutoff and purchased package expiry",
        "wallet debit, source transfer, refund bounds and idempotency",
        "PT credit consumption and idempotent refund",
        "class, room, coach and member validity"
    ];

    // Xem trạng thái áp dụng các ràng buộc thời gian.
    [HttpGet]
    public IActionResult Get() => Ok(Status());

    // Quản lý bật hoặc tắt ràng buộc thời gian để thử nghiệm.
    [HttpPut]
    [Authorize(Roles = "Manager")]
    public async Task<IActionResult> Put(UpdateFlow2TimeRulesRequest request, CancellationToken ct)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) ||
            !await users.IsActiveInRoleAsync(actorId, UserRole.Manager, ct))
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                errorCode = "ACTOR_INACTIVE",
                message = "Chỉ Manager đang hoạt động được thay đổi ràng buộc thời gian.",
            });

        if (request.EnforceTimeRules is not { } enforceTimeRules)
            return BadRequest(new
            {
                errorCode = "VALIDATION_ERROR",
                message = "enforceTimeRules is required and must be true or false.",
            });

        if (!timeRules.TrySetEnforcement(enforceTimeRules))
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                errorCode = "FLOW2_TIME_RULES_SWITCH_DISABLED",
                message = "Global Flow 2 time rules switch is disabled by server configuration.",
                available = false,
                enforceTimeRules = true,
            });

        logger.LogInformation("Manager {ActorId} set global Flow 2 EnforceTimeRules to {EnforceTimeRules}",
            actorId, enforceTimeRules);
        return Ok(Status());
    }

    private object Status() => new
    {
        available = timeRules.IsAvailable,
        enforceTimeRules = timeRules.EnforceTimeRules,
        canManage = timeRules.IsAvailable && User.IsInRole("Manager"),
        scope = "global",
        resetsOnRestart = true,
        bypassedTimeGuards = BypassedTimeGuards,
        preservedIntegrityGuards = PreservedIntegrityGuards,
    };
}
