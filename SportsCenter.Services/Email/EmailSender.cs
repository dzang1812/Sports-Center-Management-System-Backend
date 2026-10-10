using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SportsCenter.Services.Email;

// Bound from config section "Smtp". Password is a secret: use user-secrets/env var. Leave Host empty to just log mails (dev).
public class MailOptions
{
    public const string SectionName = "Smtp";

    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string Username { get; set; } = "";
    public bool EnableSsl { get; set; } = true;
    public string DisplayName { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";
    public string FrontendUrl { get; set; } = "http://localhost:5173";
}

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body);
}

public class SmtpEmailSender(IOptions<MailOptions> options, ILogger<SmtpEmailSender> log) : IEmailSender
{
    private readonly MailOptions _o = options.Value;

    public async Task SendAsync(string to, string subject, string body)
    {
        if (string.IsNullOrWhiteSpace(_o.Host))
        {
            log.LogInformation("Mail not configured, would send to {To}: {Subject}\n{Body}", to, subject, body);
            return;
        }

        using var client = new SmtpClient(_o.Host, _o.Port) { EnableSsl = _o.EnableSsl, Credentials = new NetworkCredential(_o.Username, _o.Password) };
        var from = new MailAddress(string.IsNullOrWhiteSpace(_o.From) ? _o.Username : _o.From, _o.DisplayName);
        await client.SendMailAsync(new MailMessage(from, new MailAddress(to)) { Subject = subject, Body = body });
    }
}
