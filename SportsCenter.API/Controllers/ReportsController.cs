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
    [HttpGet("revenue-by-item")] public Task<RevenueByItemReport> RevenueByItem(DateOnly from, DateOnly to, CancellationToken ct) => service.GetRevenueByItemAsync(ActorId, from, to, ct);
    [HttpGet("wallet-receipts")]
    public Task<WalletReceiptReport> WalletReceipts(DateOnly from, DateOnly to, string? search = null,
        int page = 1, int pageSize = 20, CancellationToken ct = default) =>
        service.GetWalletReceiptsAsync(ActorId, from, to, search, page, pageSize, ct);
}
