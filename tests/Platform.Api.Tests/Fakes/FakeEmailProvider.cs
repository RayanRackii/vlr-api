using System.Text.RegularExpressions;
using Platform.Api.Notifications;

namespace Platform.Api.Tests.Fakes;

public sealed class FakeEmailProvider : IEmailProvider
{
    public List<SentEmail> Sent { get; } = [];

    public int SendCount => Sent.Count;

    public bool ThrowHttpRequestException { get; set; }

    public Task SendAsync(
        string recipient,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        if (ThrowHttpRequestException)
        {
            throw new HttpRequestException("Email provider unavailable.");
        }

        Sent.Add(new SentEmail(recipient, subject, body));
        return Task.CompletedTask;
    }

    public static string ExtractSixDigitCode(string body)
    {
        var match = Regex.Match(
            body,
            @"font-family:Consolas,Monaco,monospace[^>]*>\s*(\d{6})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new InvalidOperationException("No 6-digit code found in email body.");
        }

        return match.Groups[1].Value;
    }

    public sealed record SentEmail(string Recipient, string Subject, string Body);
}
