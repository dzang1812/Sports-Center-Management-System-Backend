using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Auth;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.Services.Ai;

public interface IAiService
{
    Task<ChatResponseDto> ChatAsync(int userId, string message, CancellationToken ct = default);
    Task<ChatResponseDto> GenerateWorkoutPlanAsync(int userId, GenerateWorkoutPlanRequestDto request, CancellationToken ct = default);
    Task ApplySuggestionsAsync(int userId, ApplySuggestionsRequestDto dto, CancellationToken ct = default);
}

public partial class AiService(
    HttpClient http, IConfiguration config, IAiRepository repo, IAiUsageService usage, IAppClock clock) : IAiService
{
    private const string TextModel = "gpt-4o-mini";
    private const string VisionModel = "gpt-4.1-mini";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Posts one chat-completions request, records its real cost, and returns the message content.</summary>
    private async Task<string?> CompleteAsync(int userId, AiFeature feature, object body, string model, CancellationToken ct)
    {
        var apiKey = config["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey)) throw new AuthException("Thiếu khóa API của OpenAI.");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new AuthException($"OpenAI trả về lỗi {(int)response.StatusCode}. Vui lòng thử lại sau.");

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.TryGetProperty("usage", out var u))
            await usage.RecordCostAsync(userId, feature, model,
                u.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0,
                u.TryGetProperty("completion_tokens", out var c) ? c.GetInt32() : 0, ct);

        return root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
    }

    /// <summary>
    /// Applies the AI's create_plan / create_session / add_exercise / update_exercise / remove_exercise actions.
    /// Placeholders ("{planId}", "{sessionId_Monday}") are resolved to the rows created earlier in the same batch.
    /// </summary>
    public async Task ApplySuggestionsAsync(int userId, ApplySuggestionsRequestDto dto, CancellationToken ct = default)
    {
        if (!await usage.HasActiveSubscriptionAsync(userId, ct))
            throw new AuthException("Tính năng AI chỉ dành cho hội viên có gói đang hoạt động.");

        int? newPlanId = null;
        var createdSessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int? lastSessionId = null;

        foreach (var s in dto.Suggestions)
        {
            var planId = IsPlaceholder(s.PlanId, "{planId}") ? newPlanId : ParseId(s.PlanId);
            int? sessionId;
            if (s.SessionId.StartsWith("{sessionId_", StringComparison.Ordinal))
                sessionId = createdSessions.TryGetValue(s.SessionId.Replace("{sessionId_", "").Replace("}", "").Trim(), out var id) ? id : null;
            else if (s.SessionId == "{sessionId}") sessionId = lastSessionId;
            else sessionId = ParseId(s.SessionId);
            var exerciseId = ParseId(s.ExerciseId);

            switch (s.Action.Trim().ToLowerInvariant())
            {
                case "create_plan":
                    await repo.DeactivatePlansAsync(userId, ct); // only one active plan per member
                    var plan = new TrainingPlan
                    {
                        MemberId = userId, Title = s.PlanName, Goal = s.Goal, Description = s.PlanDescription,
                        DaysPerWeek = s.DaysPerWeek, IsActive = true, CreatedByAi = true,
                        StartDate = DateOnly.FromDateTime(clock.LocalNow),
                    };
                    repo.AddPlan(plan);
                    await repo.SaveChangesAsync(ct);
                    newPlanId = plan.Id;
                    break;

                case "create_session":
                    if (planId is null || !Enum.TryParse<DayOfWeek>(s.DayOfWeek.Trim(), true, out var day)) break;
                    var owner = await repo.GetOwnedPlanAsync(userId, planId.Value, ct);
                    if (owner is null) break;
                    var session = new TrainingPlanSession { DayOfWeek = day, Focus = s.Focus };
                    owner.Sessions.Add(session);
                    await repo.SaveChangesAsync(ct);
                    createdSessions[s.DayOfWeek.Trim()] = session.Id;
                    lastSessionId = session.Id;
                    break;

                case "add_exercise":
                    if (planId is null || sessionId is null || exerciseId is null) break;
                    var addTo = await repo.GetOwnedPlanAsync(userId, planId.Value, ct);
                    if (addTo is null || addTo.Sessions.All(x => x.Id != sessionId) || await repo.GetExerciseAsync(exerciseId.Value, ct) is null) break;
                    addTo.Items.Add(new TrainingPlanItem
                    {
                        ExerciseId = exerciseId.Value, SessionId = sessionId,
                        OrderNo = addTo.Items.Count(i => i.SessionId == sessionId) + 1,
                        Sets = s.Sets, RepsText = s.Reps, Reps = FirstNumber(s.Reps), Notes = s.Notes,
                    });
                    await repo.SaveChangesAsync(ct);
                    break;

                case "update_exercise" or "remove_exercise":
                    if (planId is null || sessionId is null || exerciseId is null) break;
                    var target = await repo.GetOwnedPlanAsync(userId, planId.Value, ct);
                    var item = target?.Items.FirstOrDefault(i => i.SessionId == sessionId && i.ExerciseId == exerciseId);
                    if (target is null || item is null) break;
                    if (s.Action.Trim().Equals("remove_exercise", StringComparison.OrdinalIgnoreCase)) target.Items.Remove(item);
                    else (item.Sets, item.RepsText, item.Reps, item.Notes) = (s.Sets, s.Reps, FirstNumber(s.Reps), s.Notes);
                    await repo.SaveChangesAsync(ct);
                    break;
            }
        }
    }

    private static bool IsPlaceholder(string value, string placeholder) =>
        string.IsNullOrWhiteSpace(value) || value == placeholder;

    private static int? ParseId(string value) => int.TryParse(value, out var id) ? id : null;

    private static int? FirstNumber(string text) =>
        System.Text.RegularExpressions.Regex.Match(text ?? "", @"\d+") is { Success: true } m ? int.Parse(m.Value) : null;
}
