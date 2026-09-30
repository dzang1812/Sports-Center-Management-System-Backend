using System.ComponentModel.DataAnnotations;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Services.Auth;

public record RegisterRequest(
    [param: Required(ErrorMessage = "Email là bắt buộc"), EmailAddress(ErrorMessage = "Email sai định dạng")] string Email,
    [param: Required(ErrorMessage = "Mật khẩu là bắt buộc"),
     RegularExpression(@"^(?=.*[A-Za-z])(?=.*\d).{8,}$", ErrorMessage = "Mật khẩu tối thiểu 8 ký tự, gồm chữ và số")] string Password,
    [param: Required(ErrorMessage = "Họ tên là bắt buộc")] string FullName,
    string? Phone);

public record LoginRequest([param: Required] string Email, [param: Required] string Password);

public record AuthResponse(string Token, DateTime ExpiresAt, int UserId, string FullName, UserRole Role, bool MustChangePassword, bool EmailVerified);

// Thrown for expected auth failures (duplicate email, bad credentials); the API maps it to 400/401.
public class AuthException(string message) : Exception(message);

public record VerifyEmailRequest([param: Required] string Token);
