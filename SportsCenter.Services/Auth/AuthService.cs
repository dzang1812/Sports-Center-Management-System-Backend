using System.IdentityModel.Tokens.Jwt;
using System.Net;
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
                $"Chào {user.FullName},\n\nBấm vào link để đặt lại mật khẩu (hết hạn sau 15 phút, chỉ dùng 1 lần):\n{link}\n\nNếu bạn không yêu cầu, hãy bỏ qua email này.");
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
            BuildVerificationHtml(user.FullName, link));
    }

    private static string BuildVerificationHtml(string fullName, string link)
    {
        var name = WebUtility.HtmlEncode(fullName);
        var href = WebUtility.HtmlEncode(link);
        return $$"""
            <!doctype html>
            <html lang="vi"><head><meta charset="utf-8"></head>
            <body style="margin:0;padding:0;background-color:#eef0f5;font-family:Arial,Helvetica,sans-serif;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background-color:#eef0f5;padding:40px 16px;">
              <tr><td align="center">
                <table role="presentation" width="480" cellpadding="0" cellspacing="0" style="max-width:480px;width:100%;background-color:#ffffff;border-radius:12px;overflow:hidden;box-shadow:0 2px 8px rgba(16,24,40,0.08);">
                  <tr><td style="padding:32px 32px 8px 32px;" align="center">
                    <span style="font-size:22px;font-weight:800;color:#4338ca;letter-spacing:0.5px;">FIT<span style="color:#0b1c30;">ZONE</span></span>
                    <div style="font-size:11px;letter-spacing:2px;color:#8a8fa3;margin-top:2px;">SPORTS CENTER</div>
                  </td></tr>
                  <tr><td style="padding:16px 32px 0 32px;" align="center"><h1 style="margin:0;font-size:20px;color:#1d2433;">Xác thực email</h1></td></tr>
                  <tr><td style="padding:20px 32px 0 32px;color:#3c4257;font-size:14px;line-height:1.6;">
                    Chào {{name}},<br><br>FitZone gửi bạn link để xác thực email cho tài khoản của bạn.
                  </td></tr>
                  <tr><td style="padding:28px 32px 8px 32px;" align="center">
                    <a href="{{href}}" target="_blank" style="display:inline-block;background-color:#4338ca;color:#ffffff;text-decoration:none;font-size:15px;font-weight:700;padding:14px 48px;border-radius:8px;">Verify</a>
                  </td></tr>
                  <tr><td style="padding:8px 32px 0 32px;" align="center"><span style="font-size:12px;color:#8a8fa3;font-style:italic;">Link có hiệu lực trong 24 giờ.</span></td></tr>
                  <tr><td style="padding:24px 32px 32px 32px;">
                    <div style="background-color:#fff7e6;border:1px solid #ffe4a3;border-radius:8px;padding:12px 16px;font-size:13px;color:#7a5b00;">Nếu bạn không yêu cầu xác thực này, vui lòng bỏ qua email.</div>
                  </td></tr>
                  <tr><td style="background-color:#0b1c30;padding:18px 24px;" align="center">
                    <span style="font-size:11px;color:#aeb4c4;">Đây là email tự động, vui lòng không phản hồi email này.</span><br>
                    <span style="font-size:11px;color:#6b7390;">© 2026 FitZone Sports Center</span>
                  </td></tr>
                </table>
              </td></tr>
            </table>
            </body></html>
            """;
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
