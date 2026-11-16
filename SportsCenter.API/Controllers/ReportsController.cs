using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Repository.Entities;
using SportsCenter.Services.Membership;

namespace SportsCenter.API.Controllers;

[ApiController, Authorize(Roles = "Manager"), Route("api/reports")]
public sealed class ReportsController(IMembershipReportService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    // Xem tổng doanh thu dịch vụ trong khoảng ngày.
    [HttpGet("revenue")] public Task<RevenueReport> Revenue(DateOnly from, DateOnly to, CancellationToken ct) => service.GetRevenueReportAsync(ActorId, from, to, ct);
    // Xem doanh thu theo khóa, tập riêng và gói.
    [HttpGet("revenue-by-item")] public Task<RevenueByItemReport> RevenueByItem(DateOnly from, DateOnly to, CancellationToken ct) => service.GetRevenueByItemAsync(ActorId, from, to, ct);
    // Tra sổ tiền nạp, mua và hoàn.
    [HttpGet("money-log")]
    public Task<MoneyLogReport> MoneyLog(DateOnly from, DateOnly to, MoneyLogType? type = null, PaymentStatus? status = null,
        string? search = null, int page = 1, int pageSize = 20, CancellationToken ct = default) =>
        service.GetMoneyLogAsync(ActorId, from, to, type, status, search, page, pageSize, ct);
    // Quản lý biên nhận tiền nạp ví.
    [HttpGet("wallet-receipts")]
    public Task<WalletReceiptReport> WalletReceipts(DateOnly from, DateOnly to, string? search = null,
        int page = 1, int pageSize = 20, CancellationToken ct = default) =>
        service.GetWalletReceiptsAsync(ActorId, from, to, search, page, pageSize, ct);
}
