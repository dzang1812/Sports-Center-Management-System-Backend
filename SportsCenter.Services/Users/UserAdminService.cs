using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Auth;

namespace SportsCenter.Services.Users;

public enum UserStatus { Active, Locked }

public record UserListItem(int Id, string Email, string FullName, string? Phone, UserRole Role, bool IsActive, DateTime CreatedAt, string[] Specialties, string? AvatarUrl, bool EmailVerified);

public record PagedResult<T>(List<T> Items, int Total, int Page, int PageSize);

// Manager creates Coach / Receptionist accounts; Specialties are sport names (Gym, Boxing, Yoga, GroupX), Coach only.
public record CreateUserRequest(
    [param: Required(ErrorMessage = "Email là bắt buộc"), EmailAddress(ErrorMessage = "Email sai định dạng")] string Email,
    [param: Required(ErrorMessage = "Họ tên là bắt buộc")] string FullName,
    string? Phone, UserRole Role, string[]? Specialties, string? AvatarUrl = null);

// Email is not editable.
public record UpdateUserRequest([param: Required(ErrorMessage = "Họ tên là bắt buộc")] string FullName, string? Phone,
    string[]? Specialties, string? AvatarUrl = null);

public record CreatedUserResponse(UserListItem User, string TempPassword);

public record SetStatusRequest(bool IsActive);

public interface IUserAdminService
{
    Task<PagedResult<UserListItem>> ListAsync(UserRole? role, UserStatus? status, string? q, int page, int pageSize);
    Task<CreatedUserResponse> CreateAsync(int actorId, CreateUserRequest request);
    Task<UserListItem> UpdateAsync(int actorId, int id, UpdateUserRequest request);
    Task SetStatusAsync(int actorId, int id, bool isActive);
    Task VerifyEmailAsync(int actorId, int id);
}

public class UserAdminService(IUserRepository users, IUserAdminRepository admin, IAuditLogRepository audit) : IUserAdminService
{
    public async Task<PagedResult<UserListItem>> ListAsync(UserRole? role, UserStatus? status, string? q, int page, int pageSize)
    {
        pageSize = pageSize is 10 or 20 or 50 ? pageSize : 10;
        page = Math.Max(page, 1);

        var (items, total) = await admin.SearchAsync(role, status is { } st ? st == UserStatus.Active : null, q, page, pageSize);

        return new PagedResult<UserListItem>(items.Select(ToItem).ToList(), total, page, pageSize);
    }

    public async Task<CreatedUserResponse> CreateAsync(int actorId, CreateUserRequest r)
    {
        if (r.Role is not (UserRole.Coach or UserRole.Receptionist))
            throw new AuthException("Chỉ tạo được tài khoản Huấn luyện viên hoặc Lễ tân");

        var email = r.Email.Trim().ToLowerInvariant();
        if (await users.EmailExistsAsync(email))
            throw new AuthException("Email đã tồn tại");

        var tempPassword = "Sc1-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
        var user = new User
        {
            Email = email,
            FullName = r.FullName.Trim(),
            Phone = r.Phone,
            Role = r.Role,
            AvatarUrl = r.AvatarUrl,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(tempPassword),
            MustChangePassword = true,
            EmailVerified = true, // manager-created staff skip the email verification gate
        };
        if (r.Role == UserRole.Coach)
            user.CoachProfile = new CoachProfile { Sports = await FindSports(r.Specialties) };

        await users.AddAsync(user);
        await users.SaveChangesAsync(); // need the id for the audit row
        Audit(actorId, "User.Create", user.Id, new { user.Email, Role = user.Role.ToString() });
        await users.SaveChangesAsync();

        return new CreatedUserResponse(ToItem(user), tempPassword);
    }

    public async Task<UserListItem> UpdateAsync(int actorId, int id, UpdateUserRequest r)
    {
        var user = await admin.GetByIdWithCoachAsync(id)
                   ?? throw new AuthException("Tài khoản không tồn tại");
        if (user.Role is not (UserRole.Coach or UserRole.Receptionist))
            throw new AuthException("Chỉ sửa được tài khoản Huấn luyện viên hoặc Lễ tân");

        user.FullName = r.FullName.Trim();
        user.Phone = r.Phone;
        user.AvatarUrl = r.AvatarUrl;
        if (user.Role == UserRole.Coach)
        {
            user.CoachProfile ??= new CoachProfile();
            user.CoachProfile.Sports = await FindSports(r.Specialties);
        }

        Audit(actorId, "User.Update", id, new { user.FullName, user.Phone, r.Specialties });
        await users.SaveChangesAsync();
        return ToItem(user);
    }

    public async Task SetStatusAsync(int actorId, int id, bool isActive)
    {
        if (id == actorId && !isActive) throw new AuthException("Không thể khoá chính mình");

        var user = await admin.GetByIdIncludingLockedAsync(id) ?? throw new AuthException("Tài khoản không tồn tại");
        user.IsActive = isActive;
        Audit(actorId, isActive ? "User.Unlock" : "User.Lock", id, new { user.Email });
        await users.SaveChangesAsync();
    }

    // For when the verification email never arrives — Manager confirms the account by hand instead.
    public async Task VerifyEmailAsync(int actorId, int id)
    {
        var user = await admin.GetByIdIncludingLockedAsync(id) ?? throw new AuthException("Tài khoản không tồn tại");
        if (user.EmailVerified) throw new AuthException("Email đã được xác thực");
        user.EmailVerified = true;
        Audit(actorId, "User.VerifyEmailManual", id, new { user.Email });
        await users.SaveChangesAsync();
    }

    private async Task<List<Sport>> FindSports(string[]? names)
    {
        names = (names ?? []).Distinct().ToArray();
        var sports = await admin.GetSportsByNamesAsync(names);
        if (sports.Count != names.Length)
            throw new AuthException("Chuyên môn không hợp lệ: " + string.Join(", ", names.Except(sports.Select(s => s.Name))));
        return sports;
    }

    private void Audit(int actorId, string action, int userId, object details) =>
        audit.Add(new AuditLog
        {
            UserId = actorId,
            Action = action,
            EntityName = nameof(User),
            EntityId = userId.ToString(),
            Details = JsonSerializer.Serialize(details),
        });

    private static UserListItem ToItem(User u) => new(
        u.Id, u.Email, u.FullName, u.Phone, u.Role, u.IsActive, u.CreatedAt,
        u.CoachProfile?.Sports.Select(s => s.Name).ToArray() ?? [], u.AvatarUrl, u.EmailVerified);
}
