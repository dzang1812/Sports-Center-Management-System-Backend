using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Services.Ai;
using SportsCenter.Services.Auth;

namespace SportsCenter.API.Controllers;

/// <summary>AI Q&amp;A for visitors who are not logged in: a few questions per IP per day, nothing else.</summary>
[ApiController, AllowAnonymous]
[Route("api/public/ai")]
public class PublicAiController(IAiService ai, IAiUsageService usage) : ControllerBase
{
    private const int MaxMessageLength = 300;

    [HttpPost("chat")]
    public async Task<IActionResult> Chat(PublicChatRequestDto dto, CancellationToken ct)
    {
        var message = dto.Message?.Trim() ?? "";
        if (message.Length == 0) return BadRequest(new { message = "Vui lòng nhập câu hỏi." });
        if (message.Length > MaxMessageLength)
            return BadRequest(new { message = $"Câu hỏi tối đa {MaxMessageLength} ký tự." });
        dto.Message = message;

        var check = await usage.CheckAnonymousAsync(HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown", ct);
        if (!check.Allowed)
            return StatusCode(StatusCodes.Status429TooManyRequests, new { code = check.Code, message = check.Message });
        try
        {
            return Ok(new PublicChatResponseDto { Response = await ai.PublicChatAsync(dto, ct), Remaining = check.Remaining ?? 0 });
        }
        catch (AuthException ex) { return BadRequest(new { message = ex.Message }); }
    }
}
