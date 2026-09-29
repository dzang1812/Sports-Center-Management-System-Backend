using SportsCenter.Repository.Entities;

namespace SportsCenter.Services.Auth;

public record RegisterRequest(string Email, string Password, string FullName, string? Phone);

public record LoginRequest(string Email, string Password);

public record AuthResponse(string Token, DateTime ExpiresAt, int UserId, string FullName, UserRole Role);

// Thrown for expected auth failures (duplicate email, bad credentials); the API maps it to 400/401.
public class AuthException(string message) : Exception(message);
