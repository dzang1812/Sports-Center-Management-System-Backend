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
public record AdminCoachReviewItem(int Id, int CoachId, string CoachName, int MemberId, string MemberName, int Rating, string? Comment, DateTime CreatedAt, DateTime UpdatedAt);

public sealed class CoachReviewService(ICoachReviewRepository reviews, ICoachProfileRepository profiles, IUserRepository users,
    IAuditLogRepository audit, INotificationRepository notifications, IAppClock clock)
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
        var member = await MemberAsync(memberId);
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
        var isNew = review is null;
        if (!isNew && review!.Rating == request.Rating && review.Comment == comment)
            return new(coach.Id, coach.FullName, coach.AvatarUrl, review.Rating, review.Comment, review.UpdatedAt); // re-save without changes: no audit, no duplicate notification
        if (review is null) reviews.Add(review = new CoachReview { CoachId = coachId, MemberId = memberId, CreatedAt = clock.UtcNow });
        review.Rating = request.Rating; review.Comment = comment; review.UpdatedAt = clock.UtcNow;
        audit.AddAudit(memberId, "CoachReview.Upsert", nameof(CoachReview), coachId, new { request.Rating });
        notifications.AddNotification(coachId, memberId, NotificationType.General,
            isNew ? "Bạn nhận được đánh giá mới" : "Học viên đã cập nhật đánh giá",
            $"{ShortName(member.FullName)} đánh giá bạn {request.Rating}/5 sao" + (comment is null ? "." : $": \"{Clip(comment, 200)}\""),
            $"coach-review:{coachId}:{memberId}:{review.UpdatedAt.Ticks}");
        await reviews.SaveAsync(ct);
        return new(coach.Id, coach.FullName, coach.AvatarUrl, review.Rating, review.Comment, review.UpdatedAt);
    }

    // Staff moderation: full member names (unlike the public list), newest first, optionally one coach.
    public async Task<List<AdminCoachReviewItem>> ListAllAsync(int actorId, int? coachId, CancellationToken ct)
    {
        await StaffAsync(actorId, "COACH_REVIEW_FORBIDDEN", "Chỉ quản lý hoặc lễ tân được quản lý đánh giá HLV.");
        return (await reviews.ListAllAsync(coachId, ct)).Select(r => new AdminCoachReviewItem(r.Id, r.CoachId, r.Coach.FullName,
            r.MemberId, r.Member.FullName, r.Rating, r.Comment, r.CreatedAt, r.UpdatedAt)).ToList();
    }

    // Removes an inappropriate review; the member is told why and may review again.
    public async Task DeleteAsync(int actorId, int reviewId, string? reason, CancellationToken ct)
    {
        await StaffAsync(actorId, "COACH_REVIEW_FORBIDDEN", "Chỉ quản lý hoặc lễ tân được quản lý đánh giá HLV.");
        reason = reason?.Trim();
        if (reason?.Length > 500) throw Invalid("Lý do tối đa 500 ký tự.");
        var review = await reviews.GetByIdAsync(reviewId, ct)
            ?? throw SchedulingException.NotFound("COACH_REVIEW_NOT_FOUND", "Không tìm thấy đánh giá.");
        reviews.Remove(review);
        audit.AddAudit(actorId, "CoachReview.Delete", nameof(CoachReview), reviewId,
            new { review.CoachId, review.MemberId, review.Rating, review.Comment, reason });
        notifications.AddNotification(review.MemberId, actorId, NotificationType.General, "Đánh giá HLV của bạn đã bị gỡ",
            $"Đánh giá {review.Rating}/5 sao cho HLV {review.Coach.FullName} đã bị gỡ" + (string.IsNullOrEmpty(reason) ? "." : $". Lý do: {reason}"),
            $"coach-review:{reviewId}:deleted");
        await reviews.SaveAsync(ct);
    }

    public async Task<CoachRatingDto> SetFeaturedAsync(int actorId, int coachId, bool featured, CancellationToken ct)
    {
        await StaffAsync(actorId, "COACH_FEATURE_FORBIDDEN", "Chỉ quản lý hoặc lễ tân được đánh dấu HLV tiêu biểu.");
        var coach = await profiles.GetActiveCoachAsync(coachId, true, ct)
            ?? throw SchedulingException.NotFound("COACH_NOT_FOUND", "Không tìm thấy HLV đang hoạt động.");
        var profile = coach.CoachProfile ??= new CoachProfile { UserId = coachId };
        profile.IsFeatured = featured;
        profile.FeaturedAt = featured ? clock.UtcNow : null;
        audit.AddAudit(actorId, featured ? "Coach.Feature" : "Coach.Unfeature", nameof(CoachProfile), coachId, new { featured });
        await profiles.SaveAsync(ct);
        return Rating(coachId, await reviews.GetStatsAsync(ct), featured);
    }

    private async Task<User> MemberAsync(int memberId)
    {
        var actor = await users.GetByIdAsync(memberId);
        if (actor is null || !actor.IsActive || actor.Role != UserRole.Member)
            throw SchedulingException.Forbidden("COACH_REVIEW_FORBIDDEN", "Chỉ hội viên được đánh giá HLV.");
        return actor;
    }

    private async Task StaffAsync(int actorId, string code, string message)
    {
        var actor = await users.GetByIdAsync(actorId);
        if (actor is null || !actor.IsActive || actor.Role is not (UserRole.Manager or UserRole.Receptionist))
            throw SchedulingException.Forbidden(code, message);
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

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
