namespace SportsCenter.Services.Auth;

// Bound from config section "Jwt". Key is a secret: set it via user-secrets/env var, never commit it.
public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "SportsCenter.API";
    public string Audience { get; set; } = "SportsCenter.Client";
    public int ExpiryMinutes { get; set; } = 120;
}
