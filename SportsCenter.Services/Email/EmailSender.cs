using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SportsCenter.Services.Email;

// Bound from config section "Mail". ApiKey is a secret: use user-secrets/env var. Leave ApiKey empty to just log mails (dev).
public class MailOptions
{
    public const string SectionName = "Mail";

    public string ApiKey { get; set; } = "";
    public string From { get; set; } = "onboarding@resend.dev";
    public string DisplayName { get; set; } = "";
    public string FrontendUrl { get; set; } = "http://localhost:5173";
}

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body);
}

// Sends via Resend's REST API (https://resend.com/docs/api-reference/emails/send-email) instead of raw
// SMTP: every Gmail/Workspace account available to this team either rejects app-password auth (Google
// disabled app passwords for Workspace accounts) or fails with obscure TLS errors. A plain HTTPS POST
// sidesteps all of that — no SMTP library needed.
public class ResendEmailSender(HttpClient http, IOptions<MailOptions> options, ILogger<ResendEmailSender> log) : IEmailSender
{
    private readonly MailOptions _o = options.Value;

    public async Task SendAsync(string to, string subject, string body)
    {
        if (string.IsNullOrWhiteSpace(_o.ApiKey))
        {
            log.LogInformation("Mail not configured, would send to {To}: {Subject}\n{Body}", to, subject, body);
            return;
        }

        var from = string.IsNullOrWhiteSpace(_o.DisplayName) ? _o.From : $"{_o.DisplayName} <{_o.From}>";
        using var request = new HttpRequestMessage(HttpMethod.Post, "emails")
        {
            Content = JsonContent.Create(new { from, to = new[] { to }, subject, text = body }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _o.ApiKey);

        var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            log.LogError("Resend rejected mail to {To}: {Status} {Error}", to, response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
