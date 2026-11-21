using Platform.Api.Notifications;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;

namespace Platform.Api.Services.Billing;

/// <summary>
/// One reminder of the billing calendar. <see cref="Key"/> is stable (it is part of the dedupe key and of the
/// <c>dunning_&lt;key&gt;</c> bookkeeping event); <see cref="DayOffset"/> is the civil-day distance (America/Sao_Paulo)
/// from the reference date: negative before it, positive after it.
/// </summary>
public sealed record DunningKind(string Key, int DayOffset)
{
    /// <summary>Next charge in 3 days (reference: the next due date).</summary>
    public static readonly DunningKind Upcoming = new("upcoming", -3);

    /// <summary>Charge due today.</summary>
    public static readonly DunningKind Due = new("due", 0);

    /// <summary>Overdue since yesterday (reference: the due date of the unpaid charge).</summary>
    public static readonly DunningKind Overdue1 = new("overdue_1", 1);

    public static readonly DunningKind Overdue5 = new("overdue_5", 5);

    /// <summary>The workspace became read-only (7 days overdue).</summary>
    public static readonly DunningKind ReadOnly = new("read_only", BillingDunningSchedule.ReadOnlyAfterDays);

    /// <summary>Trial ends in 5 days / tomorrow / today (reference: the trial end date).</summary>
    public static readonly DunningKind TrialEnd5 = new("trial_end_5", -5);

    public static readonly DunningKind TrialEnd1 = new("trial_end_1", -1);

    public static readonly DunningKind TrialEnd0 = new("trial_end_0", 0);

    public static readonly IReadOnlyList<DunningKind> All =
        [Upcoming, Due, Overdue1, Overdue5, ReadOnly, TrialEnd5, TrialEnd1, TrialEnd0];

    /// <summary>Type of the bookkeeping <see cref="SubscriptionEvent"/> that records the notice.</summary>
    public string EventType => SubscriptionEventTypes.DunningPrefix + Key;
}

/// <summary>
/// Pure calendar rules of dunning and trial-end notices. Every day is a civil date in America/Sao_Paulo
/// (spec amendment 12); nothing here reads the clock.
/// </summary>
public static class BillingDunningSchedule
{
    /// <summary>Days after the due date of an unpaid charge at which a provider subscription becomes read-only.</summary>
    public const int ReadOnlyAfterDays = 7;

    /// <summary>True when an overdue subscription is old enough to become read-only on <paramref name="today"/>.</summary>
    public static bool IsReadOnlyDue(DateTimeOffset pastDueSince, DateOnly today) =>
        BrazilCivil(pastDueSince).AddDays(ReadOnlyAfterDays) <= today;

    /// <summary>Notice of an active provider subscription whose next charge is due on <paramref name="nextDue"/>, if any today.</summary>
    public static DunningKind? ForActive(DateTimeOffset nextDue, DateOnly today) =>
        Match(today, BrazilCivil(nextDue), DunningKind.Upcoming, DunningKind.Due);

    /// <summary>Notice of an overdue subscription whose unpaid charge was due on <paramref name="pastDueSince"/>.</summary>
    public static DunningKind? ForPastDue(DateTimeOffset pastDueSince, DateOnly today) =>
        Match(today, BrazilCivil(pastDueSince), DunningKind.Overdue1, DunningKind.Overdue5);

    /// <summary>The read-only notice is due exactly 7 days after the due date of the unpaid charge.</summary>
    public static DunningKind? ForReadOnly(DateTimeOffset pastDueSince, DateOnly today) =>
        Match(today, BrazilCivil(pastDueSince), DunningKind.ReadOnly);

    public static DunningKind? ForTrial(DateTimeOffset trialEndsAt, DateOnly today) =>
        Match(today, BrazilCivil(trialEndsAt), DunningKind.TrialEnd5, DunningKind.TrialEnd1, DunningKind.TrialEnd0);

    /// <summary>The civil date (America/Sao_Paulo) that identifies the period a notice belongs to.</summary>
    public static DateOnly BrazilCivil(DateTimeOffset instant) =>
        Platform.Core.Infrastructure.Time.BrazilTimeZone.GetCivilDate(instant);

    /// <summary>Dedupe key: one notice per (subscription, kind, period). Fits <c>provider_event_id</c> (100).</summary>
    public static string DedupeKey(Guid subscriptionId, DunningKind kind, DateOnly period) =>
        $"dunning:{subscriptionId:N}:{kind.Key}:{period:yyyyMMdd}";

    private static DunningKind? Match(DateOnly today, DateOnly reference, params DunningKind[] candidates)
    {
        var offset = today.DayNumber - reference.DayNumber;
        foreach (var candidate in candidates)
        {
            if (candidate.DayOffset == offset)
            {
                return candidate;
            }
        }

        return null;
    }
}

public sealed record BillingEmailRecipient(string Email, string? Name);

/// <summary>What a notice says; the wording is chosen by kind and language, the values come from here.</summary>
public sealed record BillingEmailModel(string TenantName, DateOnly? Date, string PlanUrl);

/// <summary>Delivers a ready e-mail. The default sends through the platform notification queue.</summary>
public interface IBillingEmailSender
{
    ValueTask SendAsync(string recipientEmail, string subject, EmailContent content, CancellationToken cancellationToken);
}

public sealed class QueueBillingEmailSender(NotificationQueue queue) : IBillingEmailSender
{
    public ValueTask SendAsync(string recipientEmail, string subject, EmailContent content, CancellationToken cancellationToken) =>
        queue.EnqueueAsync(
            new NotificationMessage(Type: "Email", Recipient: recipientEmail, Subject: subject, Body: content.Html)
            {
                TextBody = content.Text,
            },
            cancellationToken);
}

public interface IBillingDunningMailer
{
    /// <summary>Tenant billing admins (permission <c>core.settings.billing</c>) plus the company contact e-mail, de-duplicated.</summary>
    Task<IReadOnlyList<BillingEmailRecipient>> ResolveRecipientsAsync(
        Guid tenantId,
        string? contactEmail,
        CancellationToken cancellationToken);

    /// <summary>Link of the plan page of the staff app.</summary>
    string PlanUrl { get; }

    Task SendAsync(
        DunningKind kind,
        IReadOnlyList<BillingEmailRecipient> recipients,
        BillingEmailModel model,
        CancellationToken cancellationToken);
}

public sealed class BillingDunningMailer(
    IStaffRecipientResolver staffRecipients,
    IBillingEmailSender sender,
    IConfiguration configuration,
    IHostEnvironment environment) : IBillingDunningMailer
{
    public const string PlanPath = "/configuracoes/plano";

    public const string LanguageKey = "Billing:Dunning:Language";

    public string PlanUrl => FrontendBaseUrlResolver.Resolve(configuration, environment) + PlanPath;

    public async Task<IReadOnlyList<BillingEmailRecipient>> ResolveRecipientsAsync(
        Guid tenantId,
        string? contactEmail,
        CancellationToken cancellationToken)
    {
        var staff = await staffRecipients.ResolveAsync(tenantId, Permissions.Core.SettingsBilling, cancellationToken);

        var recipients = new List<BillingEmailRecipient>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in staff)
        {
            Add(member.Email, member.FullName);
        }

        Add(contactEmail, null);
        return recipients;

        void Add(string? email, string? name)
        {
            var trimmed = email?.Trim();
            if (string.IsNullOrEmpty(trimmed) || !trimmed.Contains('@') || !seen.Add(trimmed))
            {
                return;
            }

            recipients.Add(new BillingEmailRecipient(trimmed, name));
        }
    }

    public async Task SendAsync(
        DunningKind kind,
        IReadOnlyList<BillingEmailRecipient> recipients,
        BillingEmailModel model,
        CancellationToken cancellationToken)
    {
        var language = BillingDunningEmails.NormalizeLanguage(configuration[LanguageKey]);
        foreach (var recipient in recipients)
        {
            var email = BillingDunningEmails.Render(kind, language, model, recipient.Name);
            await sender.SendAsync(recipient.Email, email.Subject, email.Content, cancellationToken);
        }
    }
}

public sealed record RenderedBillingEmail(string Subject, EmailContent Content);

/// <summary>
/// pt-BR / en / es copy of the dunning and trial-end notices. Billing notices are platform to tenant admin mail
/// (not tenant-configurable), so the copy lives here, next to the other platform e-mails
/// (<see cref="RolvixEmailLayout"/>), and not in the per-tenant notification template table.
/// </summary>
public static class BillingDunningEmails
{
    public const string PtBr = "pt-BR";

    public const string En = "en";

    public const string Es = "es";

    private sealed record Copy(string Subject, string[] Paragraphs, string Button);

    public static string NormalizeLanguage(string? language)
    {
        var value = language?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return PtBr;
        }

        if (value.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return En;
        }

        return value.StartsWith("es", StringComparison.OrdinalIgnoreCase) ? Es : PtBr;
    }

    public static RenderedBillingEmail Render(DunningKind kind, string language, BillingEmailModel model, string? recipientName)
    {
        var lang = NormalizeLanguage(language);
        var copy = Copies[(kind.Key, lang)];

        var tenant = EmailHtml.Encode(model.TenantName);
        var date = EmailHtml.Encode(FormatDate(model.Date, lang));
        var greeting = Greetings[lang];
        var name = string.IsNullOrWhiteSpace(recipientName) ? string.Empty : " " + EmailHtml.Encode(recipientName.Trim());

        var rows = new System.Text.StringBuilder();
        rows.Append($"""<tr><td style="padding:0 0 16px;{EmailStyles.Text}">{greeting}{name},</td></tr>""");
        foreach (var paragraph in copy.Paragraphs)
        {
            var text = paragraph
                .Replace("{tenant}", "<strong>" + tenant + "</strong>", StringComparison.Ordinal)
                .Replace("{date}", "<strong>" + date + "</strong>", StringComparison.Ordinal);
            rows.Append($"""<tr><td style="padding:0 0 16px;{EmailStyles.Text}">{text}</td></tr>""");
        }

        var url = EmailHtml.Encode(model.PlanUrl);
        rows.Append($"""<tr><td style="padding:4px 0 8px;"><a href="{url}" style="{EmailStyles.Button}">{copy.Button}</a></td></tr>""");
        rows.Append($"""<tr><td style="padding:16px 0 0;{EmailStyles.Muted}">{url}</td></tr>""");

        var inner = $"""<table role="presentation" width="100%" cellspacing="0" cellpadding="0">{rows}</table>""";
        var html = RolvixEmailLayout.Wrap(string.Empty, inner);
        return new RenderedBillingEmail(
            EmailHeaderText.StripControls(copy.Subject),
            EmailContent.FromRenderedHtml(html));
    }

    private static string FormatDate(DateOnly? date, string language) =>
        date is not { } value
            ? string.Empty
            : language == En
                ? value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                : value.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);

    private static readonly Dictionary<string, string> Greetings = new()
    {
        [PtBr] = "Olá",
        [En] = "Hello",
        [Es] = "Hola",
    };

    private static readonly Dictionary<(string Kind, string Language), Copy> Copies = new()
    {
        // --- upcoming charge (D-3) ---
        [("upcoming", PtBr)] = new(
            "Sua cobrança do plano Rolvix vence em 3 dias",
            ["A próxima cobrança da assinatura de {tenant} vence em {date}.", "Confira a forma de pagamento para evitar a interrupção do serviço."],
            "Ver minha assinatura"),
        [("upcoming", En)] = new(
            "Your Rolvix plan charge is due in 3 days",
            ["The next charge of the subscription of {tenant} is due on {date}.", "Check your payment method to avoid any service interruption."],
            "View my subscription"),
        [("upcoming", Es)] = new(
            "El cobro de tu plan Rolvix vence en 3 días",
            ["El próximo cobro de la suscripción de {tenant} vence el {date}.", "Revisa tu forma de pago para evitar la interrupción del servicio."],
            "Ver mi suscripción"),

        // --- due today (D0) ---
        [("due", PtBr)] = new(
            "Sua cobrança do plano Rolvix vence hoje",
            ["A cobrança da assinatura de {tenant} vence hoje ({date}).", "Pagando hoje, seu acesso continua normal."],
            "Ver minha assinatura"),
        [("due", En)] = new(
            "Your Rolvix plan charge is due today",
            ["The charge of the subscription of {tenant} is due today ({date}).", "If you pay today, your access continues as usual."],
            "View my subscription"),
        [("due", Es)] = new(
            "El cobro de tu plan Rolvix vence hoy",
            ["El cobro de la suscripción de {tenant} vence hoy ({date}).", "Si pagas hoy, tu acceso continúa con normalidad."],
            "Ver mi suscripción"),

        // --- overdue (D+1) ---
        [("overdue_1", PtBr)] = new(
            "Pagamento do plano Rolvix em atraso",
            ["Não identificamos o pagamento da assinatura de {tenant}, com vencimento em {date}.", "Regularize para evitar que o workspace fique somente leitura."],
            "Regularizar pagamento"),
        [("overdue_1", En)] = new(
            "Your Rolvix plan payment is overdue",
            ["We have not received the payment of the subscription of {tenant}, due on {date}.", "Please settle it to keep the workspace from becoming read-only."],
            "Pay now"),
        [("overdue_1", Es)] = new(
            "El pago de tu plan Rolvix está atrasado",
            ["No identificamos el pago de la suscripción de {tenant}, con vencimiento el {date}.", "Regularízalo para evitar que el workspace pase a solo lectura."],
            "Regularizar pago"),

        // --- overdue (D+5) ---
        [("overdue_5", PtBr)] = new(
            "Último aviso: plano Rolvix em atraso",
            ["A cobrança de {tenant} com vencimento em {date} continua em aberto.", "Se não for paga, o workspace passa ao modo somente leitura em 2 dias. Seus dados são mantidos."],
            "Regularizar pagamento"),
        [("overdue_5", En)] = new(
            "Final notice: your Rolvix plan is overdue",
            ["The charge of {tenant} due on {date} is still open.", "If it is not paid, the workspace becomes read-only in 2 days. Your data is kept."],
            "Pay now"),
        [("overdue_5", Es)] = new(
            "Último aviso: tu plan Rolvix está atrasado",
            ["El cobro de {tenant} con vencimiento el {date} sigue abierto.", "Si no se paga, el workspace pasará a solo lectura en 2 días. Tus datos se conservan."],
            "Regularizar pago"),

        // --- read-only (D+7) ---
        [("read_only", PtBr)] = new(
            "Seu workspace Rolvix está somente leitura",
            ["A cobrança de {tenant} com vencimento em {date} não foi paga e o workspace passou ao modo somente leitura. Seus dados estão preservados.", "Pague a cobrança em aberto para reativar o acesso completo."],
            "Reativar meu plano"),
        [("read_only", En)] = new(
            "Your Rolvix workspace is now read-only",
            ["The charge of {tenant} due on {date} was not paid and the workspace is now read-only. Your data is preserved.", "Pay the open charge to restore full access."],
            "Reactivate my plan"),
        [("read_only", Es)] = new(
            "Tu workspace Rolvix está en solo lectura",
            ["El cobro de {tenant} con vencimiento el {date} no fue pagado y el workspace pasó a solo lectura. Tus datos están preservados.", "Paga el cobro pendiente para restablecer el acceso completo."],
            "Reactivar mi plan"),

        // --- trial end (D-5, D-1, D0) ---
        [("trial_end_5", PtBr)] = new(
            "Seu teste do Rolvix termina em 5 dias",
            ["O período de teste de {tenant} termina em {date}.", "Assine para continuar usando o Rolvix sem interrupção."],
            "Assine para continuar"),
        [("trial_end_5", En)] = new(
            "Your Rolvix trial ends in 5 days",
            ["The trial of {tenant} ends on {date}.", "Subscribe to keep using Rolvix without interruption."],
            "Subscribe to continue"),
        [("trial_end_5", Es)] = new(
            "Tu prueba de Rolvix termina en 5 días",
            ["El período de prueba de {tenant} termina el {date}.", "Suscríbete para seguir usando Rolvix sin interrupciones."],
            "Suscríbete para continuar"),

        [("trial_end_1", PtBr)] = new(
            "Seu teste do Rolvix termina amanhã",
            ["O período de teste de {tenant} termina amanhã ({date}).", "Assine para continuar usando o Rolvix sem interrupção."],
            "Assine para continuar"),
        [("trial_end_1", En)] = new(
            "Your Rolvix trial ends tomorrow",
            ["The trial of {tenant} ends tomorrow ({date}).", "Subscribe to keep using Rolvix without interruption."],
            "Subscribe to continue"),
        [("trial_end_1", Es)] = new(
            "Tu prueba de Rolvix termina mañana",
            ["El período de prueba de {tenant} termina mañana ({date}).", "Suscríbete para seguir usando Rolvix sin interrupciones."],
            "Suscríbete para continuar"),

        [("trial_end_0", PtBr)] = new(
            "Seu teste do Rolvix termina hoje",
            ["O período de teste de {tenant} termina hoje ({date}).", "Assine para continuar. Depois do fim do teste, o workspace fica somente leitura."],
            "Assine para continuar"),
        [("trial_end_0", En)] = new(
            "Your Rolvix trial ends today",
            ["The trial of {tenant} ends today ({date}).", "Subscribe to continue. After the trial ends, the workspace becomes read-only."],
            "Subscribe to continue"),
        [("trial_end_0", Es)] = new(
            "Tu prueba de Rolvix termina hoy",
            ["El período de prueba de {tenant} termina hoy ({date}).", "Suscríbete para continuar. Al terminar la prueba, el workspace pasa a solo lectura."],
            "Suscríbete para continuar"),
    };
}
