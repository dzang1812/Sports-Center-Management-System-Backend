using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Services.Auth;

namespace SportsCenter.API.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(IAuthService authService) : ControllerBase
{
    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request)
    {
        try
        {
            return await authService.RegisterAsync(request);
        }
        catch (AuthException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request)
    {
        try
        {
            return await authService.LoginAsync(request);
        }
        catch (AuthException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }

    [HttpPost("verify-email")]
    public async Task<IActionResult> VerifyEmail(VerifyEmailRequest request)
    {
        try
        {
            await authService.VerifyEmailAsync(request.Token);
            return NoContent();
        }
        catch (AuthException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("resend-verification"), Authorize]
    public async Task<IActionResult> ResendVerification()
    {
        try
        {
            await authService.ResendVerificationAsync(int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!));
            return NoContent();
        }
        catch (AuthException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("forgot")]
    public async Task<IActionResult> Forgot(ForgotPasswordRequest request)
    {
        await authService.ForgotPasswordAsync(request.Email);
        return NoContent();
    }

    [HttpPost("reset")]
    public async Task<IActionResult> Reset(ResetPasswordRequest request)
    {
        try
        {
            await authService.ResetPasswordAsync(request.Token, request.NewPassword);
            return NoContent();
        }
        catch (AuthException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
