using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Services.Membership;

namespace SportsCenter.API.Controllers;

[ApiController, Authorize(Roles = "Manager"), Route("api/reports")]
public sealed class ReportsController(IMembershipReportService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpGet("revenue")] public Task<RevenueReport> Revenue(DateOnly from, DateOnly to, CancellationToken ct) => service.GetRevenueReportAsync(ActorId, from, to, ct);
}
