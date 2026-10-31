using Microsoft.Extensions.Configuration;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.Services.Ai;

public interface IAiUsageService
{
    /// <summary>Checks membership, monthly budget and daily quota, then reserves one quota slot before OpenAI is called (so retries cannot dodge the limit).</summary>
    Task<AiUsageCheckResult> CheckAndReserveAsync(int userId, AiFeature feature, CancellationToken ct = default);
    Task<bool> HasActiveSubscriptionAsync(int userId, CancellationToken ct = default);
    /// <summary>Records the real cost once OpenAI answered, from the tokens it reports.</summary>
    Task RecordCostAsync(int userId, AiFeature feature, string model, int promptTokens, int completionTokens, CancellationToken ct = default);
    Task<AiUsageSnapshotDto> GetSnapshotAsync(int userId, CancellationToken ct = default);
}

public class AiUsageService(IAiRepository repo, IAppClock clock, IConfiguration config) : IAiUsageService
{
    // USD per 1M tokens. Override per model with AiUsage:Pricing:<model>:InputPer1M / OutputPer1M.
    private static readonly Dictionary<string, (decimal In, decimal Out)> DefaultPricing = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-4o-mini"] = (0.15m, 0.60m),
        ["gpt-4.1-mini"] = (0.40m, 1.60m),
    };

    private static string Period() => DateTime.UtcNow.ToString("yyyy-MM");
    private static string Day() => DateTime.UtcNow.ToString("yyyy-MM-dd");

    public Task<bool> HasActiveSubscriptionAsync(int userId, CancellationToken ct = default) =>
        repo.HasActiveSubscriptionAsync(userId, DateOnly.FromDateTime(clock.LocalNow), ct);

    public async Task<AiUsageCheckResult> CheckAndReserveAsync(int userId, AiFeature feature, CancellationToken ct = default)
    {
        if (!await HasActiveSubscriptionAsync(userId, ct))
            return AiUsageCheckResult.Deny("SUBSCRIPTION_REQUIRED", "Tính năng AI chỉ dành cho hội viên có gói đang hoạt động.");

        var budget = await repo.GetBudgetAsync(Period(), ct);
        if (budget is not null && budget.SpentUsd >= MonthlyCapUsd())
            return AiUsageCheckResult.Deny("BUDGET_EXHAUSTED", "Hệ thống AI đã đạt giới hạn ngân sách của tháng này. Vui lòng quay lại vào tháng sau.");

        var quota = await repo.GetQuotaAsync(userId, Day(), ct);
        var (used, limit, counter) = feature switch
        {
            AiFeature.Chat => (quota?.ChatCount ?? 0, Limit("ChatDailyLimit", 100), "Chat"),
            AiFeature.GenerateWorkoutPlan => (quota?.GenerateCount ?? 0, Limit("GenerateDailyLimit", 5), "Generate"),
            AiFeature.EvaluateWorkout => (quota?.EvaluateCount ?? 0, Limit("EvaluateDailyLimit", 5), "Evaluate"),
            _ => (quota?.AnalyzeCount ?? 0, Limit("AnalyzeDailyLimit", 15), "Analyze"),
        };
        if (used >= limit)
            return AiUsageCheckResult.Deny("DAILY_LIMIT_REACHED", $"Bạn đã dùng hết {limit} lượt hôm nay cho tính năng này. Vui lòng quay lại vào ngày mai.");

        await repo.IncrementQuotaAsync(userId, Day(), counter, ct);
        return AiUsageCheckResult.Allow();
    }

    public async Task RecordCostAsync(int userId, AiFeature feature, string model, int promptTokens, int completionTokens, CancellationToken ct = default)
    {
        var (inPrice, outPrice) = Pricing(model);
        var cost = promptTokens / 1_000_000m * inPrice + completionTokens / 1_000_000m * outPrice;
        await repo.AddSpendAsync(Period(), cost, ct);
        await repo.LogUsageAsync(new AiUsageLog
        {
            UserId = userId, Feature = feature.ToString(), Model = model,
            PromptTokens = promptTokens, CompletionTokens = completionTokens, CostUsd = cost,
        }, ct);
    }

    public async Task<AiUsageSnapshotDto> GetSnapshotAsync(int userId, CancellationToken ct = default)
    {
        var budget = await repo.GetBudgetAsync(Period(), ct);
        var quota = await repo.GetQuotaAsync(userId, Day(), ct);
        return new AiUsageSnapshotDto
        {
            HasActiveSubscription = await HasActiveSubscriptionAsync(userId, ct),
            Chat = new() { Used = quota?.ChatCount ?? 0, Limit = Limit("ChatDailyLimit", 100) },
            Generate = new() { Used = quota?.GenerateCount ?? 0, Limit = Limit("GenerateDailyLimit", 5) },
            Analyze = new() { Used = quota?.AnalyzeCount ?? 0, Limit = Limit("AnalyzeDailyLimit", 15) },
            Evaluate = new() { Used = quota?.EvaluateCount ?? 0, Limit = Limit("EvaluateDailyLimit", 5) },
            BudgetExhausted = budget is not null && budget.SpentUsd >= MonthlyCapUsd(),
        };
    }

    private (decimal In, decimal Out) Pricing(string model) =>
        decimal.TryParse(config[$"AiUsage:Pricing:{model}:InputPer1M"], out var i) &&
        decimal.TryParse(config[$"AiUsage:Pricing:{model}:OutputPer1M"], out var o)
            ? (i, o)
            : DefaultPricing.GetValueOrDefault(model, (0.50m, 1.50m));

    private decimal MonthlyCapUsd() => decimal.TryParse(config["AiUsage:MonthlyBudgetUsd"], out var v) ? v : 20m;

    private int Limit(string key, int fallback) => int.TryParse(config[$"AiUsage:{key}"], out var v) ? v : fallback;
}
