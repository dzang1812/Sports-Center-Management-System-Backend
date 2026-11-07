using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SportsCenter.Services.Email;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;

namespace SportsCenter.Services.Auth;

public class AuthService(IUserRepository users, IOptions<JwtOptions> jwtOptions, IOptions<MailOptions> mailOptions,
    IEmailSender mail, ILogger<AuthService> log)
    : IAuthService
{
    private readonly JwtOptions _jwt = jwtOptions.Value;

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (await users.EmailExistsAsync(email))
            throw new AuthException("Email đã tồn tại");

        var user = new User
        {
            Email = email,
            FullName = request.FullName.Trim(),
            Phone = request.Phone,
            Role = UserRole.Member, // public self-registration is always a Member; staff accounts are created by a Manager
        };
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);

        await users.AddAsync(user);
        await users.SaveChangesAsync();
        await TrySendVerificationAsync(user); // registration must not fail just because mail is down

        return BuildToken(user);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request)
    {
        var user = await users.GetByEmailAsync(request.Email.Trim().ToLowerInvariant());
        // One message for unknown email, wrong password and locked account: reveals nothing.
        if (user is null || !user.IsActive || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            throw new AuthException("Email hoặc mật khẩu không đúng");

        return BuildToken(user);
    }

    public async Task VerifyEmailAsync(string token)
    {
        var t = await users.GetTokenAsync(Hash(token), TokenPurpose.VerifyEmail);
        if (t is null || t.ExpiresAt < DateTime.UtcNow)
            throw new AuthException("Link xác thực không hợp lệ hoặc đã hết hạn");
        // Idempotent: a token this same verification already succeeded with (double-submit from
        // React StrictMode's double effect-run, a page refresh, or an email client prescanning the
        // link) should look like success again, not fail just because it was already consumed.
        if (t.UsedAt is not null) return;

        t.UsedAt = DateTime.UtcNow;
        t.User.EmailVerified = true;
        await users.SaveChangesAsync();
    }

    public async Task ResendVerificationAsync(int userId)
    {
        var user = await users.GetByIdAsync(userId) ?? throw new AuthException("Tài khoản không tồn tại");
        if (user.EmailVerified) throw new AuthException("Email đã được xác thực");
        await SendVerificationAsync(user);
    }

    // Always completes silently so the caller can't tell whether the email exists.
    public async Task ForgotPasswordAsync(string email)
    {
        var user = await users.GetByEmailAsync(email.Trim().ToLowerInvariant());
        if (user is null || !user.IsActive) return;

        try
        {
            var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await users.AddTokenAsync(new EmailToken
            {
                UserId = user.Id,
                Purpose = TokenPurpose.ResetPassword,
                TokenHash = Hash(raw),
                ExpiresAt = DateTime.UtcNow.AddMinutes(15),
            });
            await users.SaveChangesAsync();

            var link = $"{mailOptions.Value.FrontendUrl}/reset-password?token={raw}";
            await mail.SendAsync(user.Email, "Đặt lại mật khẩu",
                $"Chào {user.FullName},\n\nBấm vào link để đặt lại mật khẩu (hết hạn sau 15 phút, chỉ dùng 1 lần):\n{link}\n\nNếu bạn không yêu cầu, hãy bỏ qua email này.",
                EmailTemplates.ResetPassword(user.FullName, link));
        }
        catch (Exception ex) { log.LogError(ex, "Could not send reset mail to {Email}", user.Email); }
    }

    public async Task ResetPasswordAsync(string token, string newPassword)
    {
        var t = await users.GetTokenAsync(Hash(token), TokenPurpose.ResetPassword);
        if (t is null || t.UsedAt is not null || t.ExpiresAt < DateTime.UtcNow || !t.User.IsActive)
            throw new AuthException("Link đặt lại mật khẩu không hợp lệ hoặc đã hết hạn");

        t.UsedAt = DateTime.UtcNow;
        t.User.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        t.User.MustChangePassword = false;
        await users.SaveChangesAsync();
    }

    private async Task TrySendVerificationAsync(User user)
    {
        try { await SendVerificationAsync(user); }
        catch (Exception ex) { log.LogError(ex, "Could not send verification mail to {Email}", user.Email); }
    }

    private async Task SendVerificationAsync(User user)
    {
        var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await users.AddTokenAsync(new EmailToken
        {
            UserId = user.Id,
            Purpose = TokenPurpose.VerifyEmail,
            TokenHash = Hash(raw),
            ExpiresAt = DateTime.UtcNow.AddHours(24),
        });
        await users.SaveChangesAsync();

        var link = $"{mailOptions.Value.FrontendUrl}/verify-email?token={raw}";
        await mail.SendAsync(user.Email, "Xác thực email",
            $"Chào {user.FullName},\n\nBấm vào link để xác thực email (hết hạn sau 24 giờ):\n{link}",
            EmailTemplates.Verification(user.FullName, link));
    }

    private static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private AuthResponse BuildToken(User user)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.FullName),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.Key));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expires = DateTime.UtcNow.AddMinutes(_jwt.ExpiryMinutes);

        var token = new JwtSecurityToken(_jwt.Issuer, _jwt.Audience, claims, expires: expires, signingCredentials: creds);

        return new AuthResponse(new JwtSecurityTokenHandler().WriteToken(token), expires, user.Id, user.FullName, user.Role, user.MustChangePassword, user.EmailVerified);
    }
}
