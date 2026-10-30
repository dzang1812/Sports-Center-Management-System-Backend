using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Ai;
using SportsCenter.Services.Auth;

namespace SportsCenter.API.Controllers;

[ApiController, Authorize(Roles = "Member")]
[Route("api/ai")]
public class AiController(IAiService ai, IAiRepository repo, IAiUsageService usage) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private ObjectResult Denied(AiUsageCheckResult check) => check.Code switch
    {
        "NOT_FOUND" => NotFound(new { message = check.Message }),
        "INVALID_STATE" => BadRequest(new { message = check.Message }),
        "SUBSCRIPTION_REQUIRED" => StatusCode(StatusCodes.Status403Forbidden, new { code = check.Code, message = check.Message }),
        _ => StatusCode(StatusCodes.Status429TooManyRequests, new { code = check.Code, message = check.Message }),
    };

    [HttpGet("usage/me")]
    public async Task<IActionResult> MyUsage(CancellationToken ct) => Ok(await usage.GetSnapshotAsync(UserId, ct));

    [HttpPost("chat")]
    public async Task<IActionResult> Chat(ChatRequestDto dto, CancellationToken ct)
    {
        var check = await usage.CheckAndReserveAsync(UserId, AiFeature.Chat, ct);
        if (!check.Allowed) return Denied(check);
        try { return Ok(await ai.ChatAsync(UserId, dto.Message, ct)); }
        catch (AuthException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("workout-plan")]
    public async Task<IActionResult> GenerateWorkoutPlan(GenerateWorkoutPlanRequestDto dto, CancellationToken ct)
    {
        var check = await usage.CheckAndReserveAsync(UserId, AiFeature.GenerateWorkoutPlan, ct);
        if (!check.Allowed) return Denied(check);
        try
        {
            var result = await ai.GenerateWorkoutPlanAsync(UserId, dto, ct);
            await LogFeatureAsync("GenerateWorkoutPlan", result.Response, System.Text.Json.JsonSerializer.Serialize(dto), ct);
            return Ok(result);
        }
        catch (AuthException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("apply")]
    public async Task<IActionResult> Apply(ApplySuggestionsRequestDto dto, CancellationToken ct)
    {
        try
        {
            await ai.ApplySuggestionsAsync(UserId, dto, ct);
            return Ok(new { success = true, message = "Áp dụng thành công" });
        }
        catch (AuthException ex) { return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message }); }
    }

    [HttpGet("history")]
    public async Task<IActionResult> History(CancellationToken ct) =>
        Ok((await repo.HistoryAsync(UserId, ct)).Select(x => new ChatHistoryDto
        {
            Role = x.Role == ChatRole.User ? "user" : "assistant", Content = x.Content, CreatedAt = x.CreatedAt,
        }));

    [HttpDelete("history")]
    public async Task<IActionResult> ClearHistory(CancellationToken ct)
    {
        await repo.ClearHistoryAsync(UserId, ct);
        return Ok(new { success = true, message = "Lịch sử chat đã được xóa." });
    }

    // Statistics are best-effort: a failed log write must never break the AI answer the member already paid for.
    private async Task LogFeatureAsync(string feature, string summary, string? request, CancellationToken ct)
    {
        try
        {
            await repo.LogFeatureUsageAsync(new AiFeatureUsageLog
            {
                UserId = UserId, Feature = feature, HasActiveSubscription = true,
                ResponseSummary = summary, RequestSnapshot = request,
            }, ct);
        }
        catch (Exception) { }
    }
}
