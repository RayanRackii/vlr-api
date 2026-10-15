using Platform.Core.Domain.Constants;

namespace Platform.Api.Notifications;

/// <summary>
/// Shared HTML chrome for transactional emails (black header/footer, Rolvix. mark).
/// </summary>
public static class RolvixEmailLayout
{
    public const string EmailVerificationSubject = "Código de verificação Rolvix";

    public const string PasswordResetSubject = "Redefinição de senha Rolvix";

    public static string Wrap(string recipientName, string innerHtmlBody, string? brandName = null)
    {
        var safeName = EmailHtml.Encode(recipientName);
        var trimmedBrand = brandName?.Trim();
        var platformBrand = string.IsNullOrWhiteSpace(trimmedBrand);
        var title = platformBrand ? "Rolvix" : EmailHtml.Encode(trimmedBrand);
        var headerMark = platformBrand ? "Rolvix." : title;
        var footer = platformBrand
            ? """
              <span style="font-weight:700;">Rolvix.</span>
              <span style="opacity:0.85;"> — Plataforma de gestão operacional</span>
              """
            : $"""<span style="font-weight:700;">{headerMark}</span>""";
        return
            $"""
            <!DOCTYPE html>
            <html lang="pt-BR">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>{title}</title>
            </head>
            <body style="margin:0;padding:0;background:#f4f4f5;font-family:Arial,Helvetica,sans-serif;color:#18181b;">
              <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#f4f4f5;padding:24px 12px;">
                <tr>
                  <td align="center">
                    <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="max-width:560px;background:#ffffff;border-radius:8px;overflow:hidden;border:1px solid #e4e4e7;">
                      <tr>
                        <td style="background:#000000;padding:16px 24px;">
                          <span style="font-size:20px;font-weight:700;letter-spacing:-0.02em;color:#ffffff;">{headerMark}</span>
                        </td>
                      </tr>
                      <tr>
                        <td style="padding:28px 24px;font-size:15px;line-height:1.55;color:#27272a;">
                          <table role="presentation" width="100%" cellspacing="0" cellpadding="0">
                            <tr>
                              <td style="padding:0 0 16px;font-size:15px;line-height:1.55;color:#27272a;">Olá {safeName},</td>
                            </tr>
                          </table>
                          {innerHtmlBody}
                        </td>
                      </tr>
                      <tr>
                        <td style="background:#000000;padding:14px 24px;font-size:12px;line-height:1.4;color:#ffffff;">
                          {footer}
                        </td>
                      </tr>
                    </table>
                  </td>
                </tr>
              </table>
            </body>
            </html>
            """;
    }

    public static bool IsAdministrativeRole(string? roleName)
    {
        if (string.IsNullOrWhiteSpace(roleName))
        {
            return false;
        }

        var trimmed = roleName.Trim();
        return trimmed.Equals(SystemRoles.Admin, StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals(SystemRoles.SuperAdmin, StringComparison.OrdinalIgnoreCase);
    }

    public static string InviteSubject(string companyName, IReadOnlyList<string>? roleNames)
    {
        var company = EmailHeaderText.StripControls(companyName);
        if (company.Length == 0)
        {
            company = "sua empresa";
        }

        var names = NormalizeRoles(roleNames);
        return names.Count > 0 && names.All(IsAdministrativeRole)
            ? $"Convite Rolvix — admin de {company}"
            : $"Convite Rolvix — {company}";
    }

    public static string InviteBody(
        string inviteUrl,
        string companyName,
        string? portalHost = null,
        string? roleName = null,
        IReadOnlyList<string>? roleNames = null)
    {
        var safeUrl = EmailHtml.Encode(inviteUrl);
        var lead = AccessSentence(
            companyName,
            roleNames ?? (string.IsNullOrWhiteSpace(roleName) ? null : [roleName]));
        var portalLine = string.IsNullOrWhiteSpace(portalHost)
            ? """
              <tr>
                <td style="padding:0 0 12px;font-size:15px;line-height:1.55;color:#27272a;">Este acesso é o <strong>painel operacional</strong> em <strong>rolvix.com.br</strong> (agenda e configurações). O site público (<em>seu-subdominio</em>.rolvix.com.br) é para clientes e usa outro login.</td>
              </tr>
              """
            : $"""
              <tr>
                <td style="padding:0 0 12px;font-size:15px;line-height:1.55;color:#27272a;">Este acesso é o <strong>painel operacional</strong> em <strong>rolvix.com.br</strong> (ativos, agenda, configurações).</td>
              </tr>
              <tr>
                <td style="padding:0 0 12px;font-size:15px;line-height:1.55;color:#27272a;">O site público para clientes é <strong>{EmailHtml.Encode(portalHost)}</strong> — login diferente (não use a senha deste convite lá).</td>
              </tr>
              """;

        return
            $"""
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0">
              <tr>
                <td style="padding:0 0 16px;font-size:15px;line-height:1.55;color:#27272a;">{lead}</td>
              </tr>
              {portalLine}
              <tr>
                <td style="padding:0 0 20px;font-size:15px;line-height:1.55;color:#27272a;">Defina sua senha neste link (válido por 7 dias):</td>
              </tr>
              <tr>
                <td style="padding:0 0 8px;">
                  <a href="{safeUrl}" style="display:inline-block;background:#000000;color:#ffffff;text-decoration:none;padding:12px 18px;border-radius:6px;font-weight:600;">
                    Definir senha
                  </a>
                </td>
              </tr>
              <tr>
                <td style="padding:16px 0 0;font-size:12px;line-height:1.55;color:#71717a;word-break:break-all;">
                  Depois, entre em <strong>https://rolvix.com.br/login</strong> com este e-mail.<br /><br />
                  Ou copie e cole o link no navegador:<br />{safeUrl}
                </td>
              </tr>
            </table>
            """;
    }

    private static string AccessSentence(string companyName, IReadOnlyList<string>? roleNames)
    {
        var safeCompany = EmailHtml.Encode(companyName);
        var names = NormalizeRoles(roleNames);
        if (names.Count > 0 && names.All(IsAdministrativeRole))
        {
            return $"Você foi convidado(a) para administrar <strong>{safeCompany}</strong> no console Rolvix.";
        }

        if (names.Count == 0)
        {
            return $"Você foi convidado(a) para acessar <strong>{safeCompany}</strong>.";
        }

        return $"Você foi convidado(a) para acessar <strong>{safeCompany}</strong> como <strong>{JoinRoles(names)}</strong>.";
    }

    private static List<string> NormalizeRoles(IReadOnlyList<string>? roleNames)
    {
        var result = new List<string>();
        if (roleNames is null)
        {
            return result;
        }

        foreach (var name in roleNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var trimmed = name.Trim();
            if (result.Any(existing => existing.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(trimmed);
        }

        return result;
    }

    private static string JoinRoles(IReadOnlyList<string> names)
    {
        var encoded = names.Select(EmailHtml.Encode).ToArray();
        if (encoded.Length == 1)
        {
            return encoded[0]!;
        }

        if (encoded.Length == 2)
        {
            return $"{encoded[0]} e {encoded[1]}";
        }

        return string.Join(", ", encoded[..^1]) + " e " + encoded[^1];
    }

    public static string RecoveryBody(string resetUrl)
    {
        var safeUrl = EmailHtml.Encode(resetUrl);

        return
            $"""
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0">
              <tr>
                <td style="padding:0 0 16px;font-size:15px;line-height:1.55;color:#27272a;">Recebemos um pedido para redefinir a senha da sua conta no console Rolvix.</td>
              </tr>
              <tr>
                <td style="padding:0 0 20px;font-size:15px;line-height:1.55;color:#27272a;">Clique no botão abaixo para escolher uma nova senha:</td>
              </tr>
              <tr>
                <td style="padding:0 0 8px;">
                  <a href="{safeUrl}" style="display:inline-block;background:#000000;color:#ffffff;text-decoration:none;padding:12px 18px;border-radius:6px;font-weight:600;">
                    Redefinir senha
                  </a>
                </td>
              </tr>
              <tr>
                <td style="padding:16px 0 0;font-size:12px;line-height:1.55;color:#71717a;word-break:break-all;">
                  Se você não pediu isso, ignore este e-mail — sua senha permanece a mesma.<br /><br />
                  Ou copie e cole o link no navegador:<br />{safeUrl}
                </td>
              </tr>
            </table>
            """;
    }

    public static string EmailVerificationBody(string code)
    {
        var safeCode = EmailHtml.Encode(code);
        return
            $"""
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0">
              <tr>
                <td style="padding:0 0 16px;font-size:15px;line-height:1.55;color:#27272a;">Seu código de verificação da Rolvix é:</td>
              </tr>
              <tr>
                <td style="padding:0 0 16px;font-size:28px;line-height:1.2;font-family:Consolas,Monaco,monospace;font-weight:700;letter-spacing:0.12em;color:#18181b;">{safeCode}</td>
              </tr>
              <tr>
                <td style="padding:0 0 16px;font-size:15px;line-height:1.55;color:#27272a;">Este código expira em 10 minutos.</td>
              </tr>
              <tr>
                <td style="padding:0;font-size:15px;line-height:1.55;color:#27272a;">Se você não solicitou este código, ignore esta mensagem.</td>
              </tr>
            </table>
            """;
    }

    public static string PasswordResetBody(string code)
    {
        var safeCode = EmailHtml.Encode(code);
        return
            $"""
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0">
              <tr>
                <td style="padding:0 0 16px;font-size:15px;line-height:1.55;color:#27272a;">Recebemos um pedido para redefinir a senha da sua conta no portal Rolvix.</td>
              </tr>
              <tr>
                <td style="padding:0 0 16px;font-size:15px;line-height:1.55;color:#27272a;">Seu código de redefinição é:</td>
              </tr>
              <tr>
                <td style="padding:0 0 16px;font-size:28px;line-height:1.2;font-family:Consolas,Monaco,monospace;font-weight:700;letter-spacing:0.12em;color:#18181b;">{safeCode}</td>
              </tr>
              <tr>
                <td style="padding:0 0 16px;font-size:15px;line-height:1.55;color:#27272a;">Este código expira em 15 minutos.</td>
              </tr>
              <tr>
                <td style="padding:0;font-size:15px;line-height:1.55;color:#27272a;">Se você não solicitou este código, ignore esta mensagem. Sua senha permanece a mesma.</td>
              </tr>
            </table>
            """;
    }
}
