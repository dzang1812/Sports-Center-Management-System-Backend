using System.ComponentModel.DataAnnotations;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Auth;

namespace SportsCenter.Services.Profile;

public record MeResponse(
    int Id, string Email, string FullName, string? Phone, DateOnly? DateOfBirth, Gender? Gender, string? AvatarUrl,
    UserRole Role, bool MustChangePassword, bool EmailVerified, decimal? HeightCm, decimal? WeightKg, TrainingGoal? Goal, Level? Level);

// Height/weight/goal/level are only stored for Members; other roles ignore them.
public record UpdateMeRequest(
    [param: Required(ErrorMessage = "Họ tên là bắt buộc")] string FullName,
    string? Phone, DateOnly? DateOfBirth, Gender? Gender, string? AvatarUrl,
    [param: Range(50, 250, ErrorMessage = "Chiều cao 50–250 cm")] decimal? HeightCm,
    [param: Range(20, 300, ErrorMessage = "Cân nặng 20–300 kg")] decimal? WeightKg,
    TrainingGoal? Goal, Level? Level);

public record ChangePasswordRequest(
    [param: Required(ErrorMessage = "Mật khẩu cũ là bắt buộc")] string OldPassword,
    [param: Required(ErrorMessage = "Mật khẩu mới là bắt buộc"),
     RegularExpression(@"^(?=.*[A-Za-z])(?=.*\d).{8,}$", ErrorMessage = "Mật khẩu tối thiểu 8 ký tự, gồm chữ và số")] string NewPassword);

public interface IMeService
{
    Task<MeResponse> GetAsync(int userId);
    Task<MeResponse> UpdateAsync(int userId, UpdateMeRequest request);
    Task ChangePasswordAsync(int userId, ChangePasswordRequest request);
}

public class MeService(IUserRepository users) : IMeService
{
    public async Task<MeResponse> GetAsync(int userId) => ToResponse(await Load(userId));

    public async Task<MeResponse> UpdateAsync(int userId, UpdateMeRequest r)
    {
        var u = await Load(userId);
        u.FullName = r.FullName.Trim();
        u.Phone = r.Phone;
        u.DateOfBirth = r.DateOfBirth;
        u.Gender = r.Gender;
        u.AvatarUrl = r.AvatarUrl;

        if (u.Role == UserRole.Member)
        {
            var p = u.MemberProfile ??= new MemberProfile();
            p.HeightCm = r.HeightCm;
            p.WeightKg = r.WeightKg;
            p.Goal = r.Goal;
            if (r.Level is { } level) p.Level = level;
        }

        await users.SaveChangesAsync();
        return ToResponse(u);
    }

    public async Task ChangePasswordAsync(int userId, ChangePasswordRequest r)
    {
        var u = await Load(userId);
        if (!BCrypt.Net.BCrypt.Verify(r.OldPassword, u.PasswordHash))
            throw new AuthException("Mật khẩu cũ không đúng");

        u.PasswordHash = BCrypt.Net.BCrypt.HashPassword(r.NewPassword);
        u.MustChangePassword = false;
        await users.SaveChangesAsync();
    }

    private async Task<User> Load(int id) =>
        await users.GetByIdWithProfileAsync(id) ?? throw new AuthException("Tài khoản không tồn tại");

    private static MeResponse ToResponse(User u) => new(
        u.Id, u.Email, u.FullName, u.Phone, u.DateOfBirth, u.Gender, u.AvatarUrl, u.Role, u.MustChangePassword, u.EmailVerified,
        u.MemberProfile?.HeightCm, u.MemberProfile?.WeightKg, u.MemberProfile?.Goal, u.MemberProfile?.Level);
}
