using System.ComponentModel.DataAnnotations;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.Services.Profile;

public record CoachSpecialty(int Id, string Name);
public record CoachProfileResponse(int Id, string FullName, string? AvatarUrl, string? Bio, string? Certifications,
    int? ExperienceYears, List<CoachSpecialty> Sports, string? StudentBeforeImageUrl, string? StudentAfterImageUrl,
    string? StudentResultSummary, bool StudentPhotoConsentConfirmed);
public record UpdateCoachProfileRequest(
    [param: StringLength(4000)] string? Bio,
    [param: StringLength(1000)] string? Certifications,
    [param: Range(0, 80)] int? ExperienceYears,
    [param: StringLength(2048)] string? StudentBeforeImageUrl,
    [param: StringLength(2048)] string? StudentAfterImageUrl,
    [param: StringLength(1000)] string? StudentResultSummary,
    bool StudentPhotoConsentConfirmed = false);

public sealed class CoachProfileService(ICoachProfileRepository profiles, IUserRepository users,
    IAuditLogRepository audit, IAppClock clock)
{
    public async Task<CoachProfileResponse> GetAsync(int actorId, int coachId, CancellationToken ct)
    {
        await ActiveActorAsync(actorId);
        var coach = await profiles.GetActiveCoachAsync(coachId, false, ct)
            ?? throw SchedulingException.NotFound("COACH_NOT_FOUND", "Không tìm thấy HLV đang hoạt động.");
        return Present(coach);
    }

    public async Task<CoachProfileResponse> GetOwnAsync(int actorId, CancellationToken ct)
    {
        await OwnActorAsync(actorId);
        var coach = await profiles.GetActiveCoachAsync(actorId, false, ct)
            ?? throw SchedulingException.NotFound("COACH_NOT_FOUND", "Không tìm thấy hồ sơ HLV.");
        return Present(coach);
    }

    public async Task<CoachProfileResponse> UpdateOwnAsync(int actorId, UpdateCoachProfileRequest request, CancellationToken ct)
    {
        await OwnActorAsync(actorId);
        // Validate before loading/changing tracked state; callers cannot set another coach's ID.
        if (request.ExperienceYears is < 0 or > 80) throw Invalid("Số năm kinh nghiệm phải từ 0 đến 80.");
        var bio = Clean(request.Bio, 4000); var certifications = Clean(request.Certifications, 1000);
        var before = ImageUrl(request.StudentBeforeImageUrl); var after = ImageUrl(request.StudentAfterImageUrl);
        var summary = Clean(request.StudentResultSummary, 1000);
        if ((before is null) != (after is null)) throw Invalid("Cần đủ cả ảnh trước tập và sau tập, hoặc bỏ cả hai ảnh.");
        var hasPhotos = before is not null;
        if (hasPhotos && before == after) throw Invalid("Vui lòng chọn hai ảnh trước và sau khác nhau.");
        if (hasPhotos && !request.StudentPhotoConsentConfirmed) throw Invalid("Cần xác nhận học viên đồng ý cho hiển thị hai ảnh này.");
        if (!hasPhotos && summary is not null) throw Invalid("Thêm đủ hai ảnh trước/sau để lưu mô tả kết quả học viên.");

        var coach = await profiles.GetActiveCoachAsync(actorId, true, ct)
            ?? throw SchedulingException.NotFound("COACH_NOT_FOUND", "Không tìm thấy hồ sơ HLV.");
        var profile = coach.CoachProfile ??= new CoachProfile { UserId = actorId };
        var samePhotos = before == profile.StudentBeforeImageUrl && after == profile.StudentAfterImageUrl;
        profile.Bio = bio; profile.Certifications = certifications; profile.ExperienceYears = request.ExperienceYears;
        profile.StudentBeforeImageUrl = before; profile.StudentAfterImageUrl = after; profile.StudentResultSummary = hasPhotos ? summary : null;
        profile.StudentPhotosConsentedAt = !hasPhotos ? null : samePhotos && profile.StudentPhotosConsentedAt is not null ? profile.StudentPhotosConsentedAt : clock.UtcNow;
        audit.AddAudit(actorId, "CoachProfile.Update", nameof(CoachProfile), actorId, new { request.ExperienceYears, hasPhotos });
        await profiles.SaveAsync(ct);
        return Present(coach);
    }

    private async Task ActiveActorAsync(int actorId)
    {
        var actor = await users.GetByIdAsync(actorId);
        if (actor is null || !actor.IsActive) throw SchedulingException.Forbidden("ACTOR_INACTIVE", "Tài khoản không hoạt động.");
    }

    private async Task OwnActorAsync(int actorId)
    {
        var actor = await users.GetByIdAsync(actorId);
        if (actor is null || !actor.IsActive || actor.Role != UserRole.Coach)
            throw SchedulingException.Forbidden("COACH_PROFILE_FORBIDDEN", "Chỉ HLV được sửa hồ sơ của chính mình.");
    }

    private static SchedulingException Invalid(string message) => new("COACH_PROFILE_INVALID", message);
    private static string? Clean(string? value, int max)
    {
        var result = value?.Trim();
        if (string.IsNullOrEmpty(result)) return null;
        if (result.Length > max) throw Invalid($"Nội dung vượt quá {max} ký tự.");
        return result;
    }
    private static string? ImageUrl(string? value)
    {
        var url = Clean(value, 2048);
        if (url is not null && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo))) throw Invalid("Ảnh phải có URL HTTPS hợp lệ.");
        return url;
    }
    private static CoachProfileResponse Present(User coach)
    {
        var p = coach.CoachProfile;
        var photosVisible = p?.StudentPhotosConsentedAt is not null && !string.IsNullOrWhiteSpace(p.StudentBeforeImageUrl)
            && !string.IsNullOrWhiteSpace(p.StudentAfterImageUrl);
        return new(coach.Id, coach.FullName, coach.AvatarUrl, p?.Bio, p?.Certifications, p?.ExperienceYears,
            p?.Sports.Where(s => s.Status == SportStatus.Active).OrderBy(s => s.Name).Select(s => new CoachSpecialty(s.Id, s.Name)).ToList() ?? [],
            photosVisible ? p!.StudentBeforeImageUrl : null, photosVisible ? p!.StudentAfterImageUrl : null,
            photosVisible ? p!.StudentResultSummary : null, photosVisible);
    }
}
