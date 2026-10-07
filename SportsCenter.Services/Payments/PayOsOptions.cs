namespace SportsCenter.Services.Payments;

// Bound from config section "PayOs". All three are secrets: set via user-secrets/env var, never commit them.
public class PayOsOptions
{
    public const string SectionName = "PayOs";

    public string ClientId { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ChecksumKey { get; set; } = "";
}
