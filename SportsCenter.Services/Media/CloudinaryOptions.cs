namespace SportsCenter.Services.Media;

// Bound from config section "Cloudinary". ApiKey/ApiSecret are secrets: set via user-secrets/env var, never commit them.
public class CloudinaryOptions
{
    public const string SectionName = "Cloudinary";

    public string CloudName { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
}
