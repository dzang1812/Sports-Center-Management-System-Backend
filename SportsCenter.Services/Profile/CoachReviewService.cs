using System.ComponentModel.DataAnnotations;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.Services.Profile;

public record CoachRatingDto(int CoachId, double AverageRating, int ReviewCount, bool IsFeatured);
public record CoachReviewItem(int Id, string MemberName, int Rating, string? Comment, DateTime UpdatedAt);
public record CoachReviewsResponse(int CoachId, double AverageRating, int ReviewCount, bool IsFeatured, List<CoachReviewItem> Items);
public record ReviewableCoachDto(int CoachId, string FullName, string? AvatarUrl, int? MyRating, string? MyComment, DateTime? ReviewedAt);
public record UpsertCoachReviewRequest([param: Range(1, 5)] int Rating, [param: StringLength(1000)] string? Comment);
public record SetCoachFeaturedRequest(bool Featured);

public sealed class CoachReviewService(ICoachReviewRepository reviews, ICoachProfileRepository profiles, IUserRepository users,
    IAuditLogRepository audit, IAppClock clock)
{
    // Public: rating + featured flag for every coach that has either, keyed by coach id on the client.
    public async Task<List<CoachRatingDto>> ListRatingsAsync(CancellationToken ct)
    {
        var stats = await reviews.GetStatsAsync(ct);
        var featured = await profiles.ListFeaturedCoachIdsAsync(ct);
        return stats.Keys.Union(featured).Select(id => Rating(id, stats, featured.Contains(id))).ToList();
    }

    public async Task<CoachReviewsResponse> ListForCoachAsync(int coachId, CancellationToken ct)
    {
        var coach = await profiles.GetActiveCoachAsync(coachId, false, ct)
            ?? throw SchedulingException.NotFound("COACH_NOT_FOUND", "Không tìm thấy HLV đang hoạt động.");
        var stats = await reviews.GetStatsAsync(ct);
        var rating = Rating(coachId, stats, coach.CoachProfile?.IsFeatured == true);
        var items = await reviews.ListForCoachAsync(coachId, 20, ct);
        return new(coachId, rating.AverageRating, rating.ReviewCount, rating.IsFeatured,
            items.Select(r => new CoachReviewItem(r.Id, ShortName(r.Member.FullName), r.Rating, r.Comment, r.UpdatedAt)).ToList());
    }

    public async Task<List<ReviewableCoachDto>> ListMineAsync(int memberId, CancellationToken ct)
    {
        await MemberAsync(memberId);
        var now = clock.LocalNow;
        var coachIds = await reviews.ListTrainedCoachIdsAsync(memberId, DateOnly.FromDateTime(now), TimeOnly.FromDateTime(now), ct);
        var coaches = await reviews.ListActiveCoachesAsync(coachIds, ct);
        var mine = (await reviews.ListByMemberAsync(memberId, ct)).ToDictionary(r => r.CoachId);
        return coaches.Select(c => mine.TryGetValue(c.Id, out var r)
            ? new ReviewableCoachDto(c.Id, c.FullName, c.AvatarUrl, r.Rating, r.Comment, r.UpdatedAt)
            : new ReviewableCoachDto(c.Id, c.FullName, c.AvatarUrl, null, null, null)).ToList();
    }

    public async Task<ReviewableCoachDto> UpsertAsync(int memberId, int coachId, UpsertCoachReviewRequest request, CancellationToken ct)
    {
        await MemberAsync(memberId);
        if (request.Rating is < 1 or > 5) throw Invalid("Vui lòng chọn từ 1 đến 5 sao.");
        var comment = request.Comment?.Trim();
        if (string.IsNullOrEmpty(comment)) comment = null;
        else if (comment.Length > 1000) throw Invalid("Nhận xét tối đa 1000 ký tự.");
        var now = clock.LocalNow;
        var trained = await reviews.ListTrainedCoachIdsAsync(memberId, DateOnly.FromDateTime(now), TimeOnly.FromDateTime(now), ct);
        if (!trained.Contains(coachId))
            throw SchedulingException.Forbidden("COACH_REVIEW_NOT_TRAINED", "Bạn chỉ có thể đánh giá HLV đã dạy bạn (lớp học hoặc PT đã diễn ra).");
        var coach = (await reviews.ListActiveCoachesAsync([coachId], ct)).FirstOrDefault()
            ?? throw SchedulingException.NotFound("COACH_NOT_FOUND", "Không tìm thấy HLV đang hoạt động.");

        var review = await reviews.GetAsync(coachId, memberId, ct);
        if (review is null) reviews.Add(review = new CoachReview { CoachId = coachId, MemberId = memberId, CreatedAt = clock.UtcNow });
        review.Rating = request.Rating; review.Comment = comment; review.UpdatedAt = clock.UtcNow;
        audit.AddAudit(memberId, "CoachReview.Upsert", nameof(CoachReview), coachId, new { request.Rating });
        await reviews.SaveAsync(ct);
        return new(coach.Id, coach.FullName, coach.AvatarUrl, review.Rating, review.Comment, review.UpdatedAt);
    }

    public async Task<CoachRatingDto> SetFeaturedAsync(int actorId, int coachId, bool featured, CancellationToken ct)
    {
        var actor = await users.GetByIdAsync(actorId);
        if (actor is null || !actor.IsActive || actor.Role is not (UserRole.Manager or UserRole.Receptionist))
            throw SchedulingException.Forbidden("COACH_FEATURE_FORBIDDEN", "Chỉ quản lý hoặc lễ tân được đánh dấu HLV tiêu biểu.");
        var coach = await profiles.GetActiveCoachAsync(coachId, true, ct)
            ?? throw SchedulingException.NotFound("COACH_NOT_FOUND", "Không tìm thấy HLV đang hoạt động.");
        var profile = coach.CoachProfile ??= new CoachProfile { UserId = coachId };
        profile.IsFeatured = featured;
        profile.FeaturedAt = featured ? clock.UtcNow : null;
        audit.AddAudit(actorId, featured ? "Coach.Feature" : "Coach.Unfeature", nameof(CoachProfile), coachId, new { featured });
        await profiles.SaveAsync(ct);
        return Rating(coachId, await reviews.GetStatsAsync(ct), featured);
    }

    private async Task MemberAsync(int memberId)
    {
        var actor = await users.GetByIdAsync(memberId);
        if (actor is null || !actor.IsActive || actor.Role != UserRole.Member)
            throw SchedulingException.Forbidden("COACH_REVIEW_FORBIDDEN", "Chỉ hội viên được đánh giá HLV.");
    }

    private static CoachRatingDto Rating(int coachId, Dictionary<int, CoachRatingStats> stats, bool featured) =>
        stats.TryGetValue(coachId, out var s) ? new(coachId, Math.Round(s.Average, 1), s.Count, featured) : new(coachId, 0, 0, featured);

    // Public page: "Nguyễn Văn An" -> "N. V. An", so reviews never expose a member's full name.
    private static string ShortName(string fullName)
    {
        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 1 ? fullName : string.Join(' ', parts[..^1].Select(p => $"{p[0]}.").Append(parts[^1]));
    }

    private static SchedulingException Invalid(string message) => new("COACH_REVIEW_INVALID", message);
}
