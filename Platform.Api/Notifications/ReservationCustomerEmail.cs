using System.Globalization;
using System.Text;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Notifications;

/// <summary>
/// Customer-facing Rentals reservation email. Rendered at send time so a stored
/// template cannot drop the date, the price, or the portal link.
/// </summary>
public static class ReservationCustomerEmail
{
    public const string TenantNameKey = "tenantName";
    public const string CustomerNameKey = "customerName";
    public const string ReferenceKey = "reservationReference";
    public const string StatusKey = "reservationStatus";
    public const string DateTimeKey = "reservationDateTime";
    public const string DateKey = "reservationDate";
    public const string StartKey = "reservationStart";
    public const string EndKey = "reservationEnd";
    public const string TotalKey = "reservationTotal";
    public const string DepositKey = "reservationDeposit";
    public const string UrlKey = "reservationUrl";

    private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");

    public static bool Applies(string eventType) =>
        eventType is RentalEventTypes.ReservationPendingDeposit
            or RentalEventTypes.ReservationConfirmed
            or RentalEventTypes.ReservationCanceled
            or RentalEventTypes.ReservationCompleted
            or RentalEventTypes.ReservationReminder;

    public static string? FormatMoney(decimal amount) =>
        amount > 0m
            ? amount.ToString("C", Portuguese)
            : null;

    public static (string Date, string Start, string End) FormatSchedule(
        DateTimeOffset start,
        DateTimeOffset end,
        TimeZoneInfo zone)
    {
        var startLocal = TimeZoneInfo.ConvertTime(start, zone);
        var endLocal = TimeZoneInfo.ConvertTime(end, zone);
        var startDate = startLocal.ToString("dd/MM/yyyy", Portuguese);
        var endDate = endLocal.ToString("dd/MM/yyyy", Portuguese);
        var date = startDate == endDate ? startDate : $"{startDate}–{endDate}";
        return (
            date,
            startLocal.ToString("HH:mm", Portuguese),
            endLocal.ToString("HH:mm", Portuguese));
    }

    public static ReservationEmailContent Render(
        string eventType,
        IReadOnlyDictionary<string, string?> payload)
    {
        var tenantName = Text(payload, TenantNameKey);
        var customerName = Text(payload, CustomerNameKey);
        var reference = Text(payload, ReferenceKey);
        var safeReference = EmailHtml.Encode(
            string.IsNullOrWhiteSpace(reference) ? "reserva" : reference);
        var date = Text(payload, DateKey);
        var start = Text(payload, StartKey);
        var end = Text(payload, EndKey);
        var total = Text(payload, TotalKey);
        var deposit = Text(payload, DepositKey);
        var url = Text(payload, UrlKey);
        var timeRange = string.IsNullOrWhiteSpace(start) || string.IsNullOrWhiteSpace(end)
            ? null
            : $"{start}–{end}";

        var inner = new StringBuilder();
        inner.Append(
            """
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0">
            """);
        AppendTextRow(inner, Intro(eventType, safeReference));
        AppendLabeledRow(inner, "Estabelecimento", tenantName);
        AppendLabeledRow(inner, "Data", date);
        AppendLabeledRow(inner, "Horário", timeRange);
        AppendLabeledRow(inner, "Total", total);
        AppendLabeledRow(inner, "Depósito", deposit);
        AppendLink(inner, url);
        inner.Append("</table>");

        var brand = string.IsNullOrWhiteSpace(tenantName) ? null : tenantName;
        var html = RolvixEmailLayout.Wrap(
            string.IsNullOrWhiteSpace(customerName) ? " " : customerName,
            inner.ToString(),
            brand);

        return new ReservationEmailContent(
            SubjectFor(eventType, reference),
            html,
            brand);
    }

    public static string SubjectFor(string eventType, string? reference)
    {
        var resource = CleanSubject(reference);
        if (resource.Length == 0)
        {
            resource = "reserva";
        }

        return eventType switch
        {
            RentalEventTypes.ReservationPendingDeposit => $"Reserva {resource} aguardando depósito",
            RentalEventTypes.ReservationConfirmed => $"Reserva {resource} confirmada",
            RentalEventTypes.ReservationCanceled => $"Reserva {resource} cancelada",
            RentalEventTypes.ReservationCompleted => $"Reserva {resource} concluída",
            RentalEventTypes.ReservationReminder => $"Lembrete de reserva {resource}",
            _ => $"Reserva {resource}",
        };
    }

    private static string Intro(string eventType, string safeReference) =>
        eventType switch
        {
            RentalEventTypes.ReservationPendingDeposit =>
                $"Sua reserva <strong>{safeReference}</strong> está aguardando depósito.",
            RentalEventTypes.ReservationConfirmed =>
                $"Sua reserva <strong>{safeReference}</strong> foi confirmada.",
            RentalEventTypes.ReservationCanceled =>
                $"Sua reserva <strong>{safeReference}</strong> foi cancelada.",
            RentalEventTypes.ReservationCompleted =>
                $"Sua reserva <strong>{safeReference}</strong> foi concluída.",
            RentalEventTypes.ReservationReminder =>
                $"Lembrete da sua reserva <strong>{safeReference}</strong>.",
            _ => $"Sua reserva <strong>{safeReference}</strong>.",
        };

    private static void AppendTextRow(StringBuilder html, string encodedHtml)
    {
        html.Append(
            """
            <tr><td style="padding:0 0 16px;font-size:15px;line-height:1.55;color:#27272a;">
            """);
        html.Append(encodedHtml);
        html.Append("</td></tr>");
    }

    private static void AppendLabeledRow(StringBuilder html, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        html.Append(
            """
            <tr><td style="padding:0 0 12px;font-size:15px;line-height:1.55;color:#27272a;">
            """);
        html.Append(EmailHtml.Encode(label));
        html.Append(": <strong>");
        html.Append(EmailHtml.Encode(value.Trim()));
        html.Append("</strong></td></tr>");
    }

    private static void AppendLink(StringBuilder html, string? url)
    {
        if (!IsSafeHttp(url))
        {
            return;
        }

        var safeUrl = EmailHtml.Encode(url);
        html.Append(
            $"""
            <tr><td style="padding:8px 0 8px;"><a href="{safeUrl}" style="display:inline-block;background:#000000;color:#ffffff;text-decoration:none;padding:12px 18px;border-radius:6px;font-weight:600;">Ver reserva</a></td></tr>
            <tr><td style="padding:0;font-size:12px;line-height:1.55;color:#71717a;word-break:break-all;">{safeUrl}</td></tr>
            """);
    }

    private static bool IsSafeHttp(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https";

    private static string? Text(IReadOnlyDictionary<string, string?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static string CleanSubject(string? value) => EmailHeaderText.StripControls(value);
}

public sealed record ReservationEmailContent(
    string Subject,
    string HtmlBody,
    string? FromDisplayName);
