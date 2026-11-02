using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace SportsCenter.Services.Training;

public record ExerciseGuideDto(string Id, string Name, string? BodyPart, string? Equipment,
    string[] Instructions, string[] Tips, string? VideoUrl, string? ImageUrl);
public record ExerciseGuideSearchDto(int Total, int Offset, List<ExerciseGuideDto> Results);
public sealed class ExerciseGuideException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public interface IExerciseGuideService
{
    Task<ExerciseGuideSearchDto> SearchAsync(string query, int offset, CancellationToken ct);
}

// Read-only integration. Provider credentials never leave the server; signed media is streamed by the browser.
public sealed class ExerciseGuideService(HttpClient http, IConfiguration config, IMemoryCache cache) : IExerciseGuideService
{
    private sealed record ProviderMedia(string? Mp4, string? Poster, DateTimeOffset? ExpiresAt);
    private sealed record ProviderExercise(string Id, string Name, string? BodyPart, string? Equipment,
        string[]? Instructions, string[]? Tips, ProviderMedia? Media);
    private sealed record ProviderPage(int Total, List<ProviderExercise>? Results);

    public async Task<ExerciseGuideSearchDto> SearchAsync(string query, int offset, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length is < 2 or > 100 || offset is < 0 or > 1000 || offset % 10 != 0)
            throw new ExerciseGuideException(400, "Nhập tên bài từ 2–100 ký tự và trang hợp lệ.");
        var key = config["WorkoutX:ApiKey"];
        if (string.IsNullOrWhiteSpace(key) || key == "REPLACE_ME")
            throw new ExerciseGuideException(503, "Hướng dẫn tập chưa được cấu hình trên Backend. Vui lòng liên hệ quản lý.");
        var cacheKey = $"workoutx:{query.ToLowerInvariant()}:{offset}";
        if (cache.TryGetValue(cacheKey, out ExerciseGuideSearchDto? existing)) return existing!;

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.workoutxapp.com/v2/exercises?name={Uri.EscapeDataString(query)}&limit=10&offset={offset}");
        request.Headers.Add("X-WorkoutX-Key", key);
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw response.StatusCode switch
                {
                    HttpStatusCode.TooManyRequests => new ExerciseGuideException(429, "WorkoutX đã đạt giới hạn lượt xem. Vui lòng thử lại sau."),
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ExerciseGuideException(503, "WorkoutX từ chối cấu hình API hiện tại. Vui lòng liên hệ quản lý."),
                    _ => new ExerciseGuideException(502, "Không tải được hướng dẫn WorkoutX. Vui lòng thử lại sau.")
                };
            var page = await response.Content.ReadFromJsonAsync<ProviderPage>(cancellationToken: ct)
                ?? throw new ExerciseGuideException(502, "WorkoutX trả về dữ liệu không hợp lệ.");
            var rows = page.Results;
            if (rows is null || page.Total < 0 || rows.Any(x => string.IsNullOrWhiteSpace(x.Id) || string.IsNullOrWhiteSpace(x.Name)))
                throw new ExerciseGuideException(502, "WorkoutX trả về dữ liệu không hợp lệ.");
            var result = new ExerciseGuideSearchDto(page.Total, offset, rows.Select(x => new ExerciseGuideDto(
                x.Id, x.Name, x.BodyPart, x.Equipment, x.Instructions ?? [], x.Tips ?? [],
                HttpsUrl(x.Media?.Mp4), HttpsUrl(x.Media?.Poster))).ToList());
            // Never cache a signed URL past its expiry, or persist/rehost provider media.
            var until = DateTimeOffset.UtcNow.AddMinutes(15);
            foreach (var row in rows)
                if (row.Media?.ExpiresAt is { } expiry && expiry.AddMinutes(-1) < until) until = expiry.AddMinutes(-1);
            if (until > DateTimeOffset.UtcNow) cache.Set(cacheKey, result, until);
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new ExerciseGuideException(504, "WorkoutX phản hồi chậm. Vui lòng thử lại."); }
        catch (HttpRequestException)
        { throw new ExerciseGuideException(502, "Không kết nối được WorkoutX. Vui lòng thử lại."); }
        catch (System.Text.Json.JsonException)
        { throw new ExerciseGuideException(502, "WorkoutX trả về dữ liệu không hợp lệ."); }
    }

    private static string? HttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" ? value : null;
}
