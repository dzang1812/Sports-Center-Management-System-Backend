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
public class AiController(IAiService ai, IAiRepository repo, IAiUsageService usage, IWorkoutEvaluationService evaluations) : ControllerBase
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

    [HttpPost("evaluate-workout/{sessionId:int}")]
    public async Task<IActionResult> EvaluateWorkout(int sessionId, CancellationToken ct)
    {
        try
        {
            var (check, report) = await evaluations.EvaluateAsync(UserId, sessionId, ct);
            return check.Allowed ? Ok(report) : Denied(check);
        }
        catch (AuthException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("analyze-image"), Consumes("multipart/form-data")]
    public async Task<IActionResult> AnalyzeImage([FromForm] AnalyzeImageRequest request, CancellationToken ct)
    {
        var image = request.Image;
        if (image is null || image.Length == 0) return BadRequest(new { message = "Vui lòng chọn ảnh." });
        if (!(new[] { "image/jpeg", "image/png", "image/webp" }).Contains(image.ContentType))
            return BadRequest(new { message = "Chỉ hỗ trợ ảnh JPG, PNG hoặc WEBP." });
        if (image.Length > 5 * 1024 * 1024) return BadRequest(new { message = "Ảnh không được vượt quá 5MB." });
        if (request.Mode is not ("equipment_info" or "body_check"))
            return BadRequest(new { message = "Mode không hợp lệ. Dùng equipment_info hoặc body_check." });

        var check = await usage.CheckAndReserveAsync(UserId, AiFeature.AnalyzeImage, ct);
        if (!check.Allowed) return Denied(check);
        try
        {
            await using var stream = image.OpenReadStream();
            var result = await ai.AnalyzeImageAsync(UserId, stream, image.ContentType, request.Mode, ct);
            await LogFeatureAsync(request.Mode == "body_check" ? "BodyCheck" : "EquipmentInfo", FormatImage(result), null, ct);
            return Ok(result);
        }
        catch (AuthException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("analyze-form-video"), Consumes("multipart/form-data")]
    public async Task<IActionResult> AnalyzeFormVideo([FromForm] AnalyzeFormVideoRequest request, CancellationToken ct)
    {
        var video = request.Video;
        if (video is null || video.Length == 0) return BadRequest(new { message = "Vui lòng tải video lên." });
        if (video.Length > 25 * 1024 * 1024) return BadRequest(new { message = "Dung lượng video vượt quá 25MB." });
        if (!(new[] { "video/mp4", "video/quicktime" }).Contains(video.ContentType))
            return BadRequest(new { message = "Chỉ hỗ trợ video MP4 hoặc MOV." });

        var check = await usage.CheckAndReserveAsync(UserId, AiFeature.AnalyzeFormVideo, ct);
        if (!check.Allowed) return Denied(check);
        try
        {
            await using var stream = video.OpenReadStream();
            var result = await ai.AnalyzeFormVideoAsync(UserId, stream, video.FileName, ct);
            await LogFeatureAsync("FormCheckVideo", FormatVideo(result), null, ct);
            return Ok(result);
        }
        catch (AuthException ex) { return BadRequest(new { message = ex.Message }); }
    }

    public class AnalyzeImageRequest
    {
        public IFormFile? Image { get; set; }
        public string? Mode { get; set; }
    }

    public class AnalyzeFormVideoRequest
    {
        public IFormFile? Video { get; set; }
    }

    private static string Join(IEnumerable<string?> parts) => string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string FormatImage(ImageAnalyzeResponseDto r) => Join([
        r.Title, r.Summary,
        r.DetectedItems.Count > 0 ? "Nhận diện: " + string.Join(", ", r.DetectedItems) : null,
        r.PriorityMuscles.Count > 0 ? "Nhóm cơ ưu tiên: " + string.Join(", ", r.PriorityMuscles) : null,
        r.SuggestedExercises.Count > 0 ? "Gợi ý bài tập: " + string.Join(", ", r.SuggestedExercises) : null,
        r.TrainingAdvice.Count > 0 ? "Lời khuyên: " + string.Join(" ", r.TrainingAdvice) : null,
        r.Warnings.Count > 0 ? "Lưu ý: " + string.Join(" ", r.Warnings) : null]);

    private static string FormatVideo(VideoFormAnalyzeResponseDto r) => Join([
        r.Title, r.Summary, $"Bài tập nhận diện: {r.DetectedExercise}", $"Kết luận: {r.OverallVerdict}",
        r.MajorIssues.Count > 0 ? "Lỗi lớn: " + string.Join(" ", r.MajorIssues) : null,
        r.MinorIssues.Count > 0 ? "Lỗi nhỏ: " + string.Join(" ", r.MinorIssues) : null,
        r.CorrectPoints.Count > 0 ? "Điểm đúng: " + string.Join(" ", r.CorrectPoints) : null,
        r.CorrectiveCues.Count > 0 ? "Cách sửa: " + string.Join(" ", r.CorrectiveCues) : null]);
}
