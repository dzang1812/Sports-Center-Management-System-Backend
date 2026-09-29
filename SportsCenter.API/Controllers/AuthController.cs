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
}
