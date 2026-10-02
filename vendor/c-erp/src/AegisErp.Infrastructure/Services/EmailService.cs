using System.Net;
using System.Net.Mail;
using AegisErp.Domain;
using Microsoft.Extensions.Options;

namespace AegisErp.Infrastructure.Services;

/// <summary>SMTP settings, bound from the "Smtp" configuration section. Empty <see cref="Host"/> means
/// email hasn't been configured — callers get a clear error instead of a silent no-op.</summary>
public class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "Aegis ERP";
}

/// <summary>Thin wrapper over SmtpClient. No queueing/retry — a document email is a one-off,
/// user-triggered action, not a background job.</summary>
public class EmailService
{
    private readonly SmtpOptions _options;
    public EmailService(IOptions<SmtpOptions> options) => _options = options.Value;

    public async Task SendAsync(string toEmail, string subject, string body)
    {
        if (string.IsNullOrWhiteSpace(_options.Host))
            throw new PostingException("Email is not configured — set the Smtp:Host setting (and related Smtp:* settings) to enable sending.");
        if (string.IsNullOrWhiteSpace(toEmail))
            throw new PostingException("Recipient email address is required.");

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromName),
            Subject = subject,
            Body = body,
        };
        message.To.Add(toEmail);

        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.EnableSsl,
            Credentials = string.IsNullOrWhiteSpace(_options.Username)
                ? null
                : new NetworkCredential(_options.Username, _options.Password),
        };
        await client.SendMailAsync(message);
    }
}
